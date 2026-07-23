using BBT.Aether.DistributedLock;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Hosting;
using BBT.Workflow.Infrastructure.Execution.Locks;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.HostedServices;

/// <summary>
/// Periodically sweeps for stuck-Busy auto-chains (instances Busy with an active chain token,
/// a stale heartbeat and no live job) and resolves them via <see cref="IChainReaperService"/>.
/// Gated by <c>WorkflowExecutionOptions.EnableChainReaper</c>.
/// </summary>
/// <remarks>
/// Multi-schema: vNext creates one database schema per loaded flow at runtime, and instances
/// live in those per-flow schemas — not in any single ambient/default schema. A hosted service
/// has no request-scoped <see cref="ICurrentSchema"/>, so this worker first discovers all flow
/// keys from <c>sys_flows</c> and then runs the reaper once per flow schema, each in its own DI
/// scope with the schema established via <c>IcurrentSchema.Change(flowKey)</c> (mirrors
/// <c>SchemaMigrationRunner</c> / <c>MultiSchemaMigrator</c>). A fresh scope per schema avoids
/// change-tracker bleed across schemas. Interval is fixed here; promote to options if needed.
/// <para>
/// Leader election: this hosted service runs on every orchestration replica, but a full sweep
/// per replica would multiply the <c>sys_flows</c> discovery and per-flow-schema polling by the
/// replica count. To avoid that redundancy, each cycle first tries to acquire a single
/// <c>chain-reaper-leader</c> lease only the winner
/// sweeps, the others skip. PostgreSQL grants exactly one winner through an atomic
/// <c>INSERT … ON CONFLICT</c>. The lease is released as soon as the sweep completes; its TTL
/// (<c>WorkflowExecutionOptions.ChainReaperLeaderLeaseSeconds</c>) is only a crash-safety net so
/// another replica can take over on the next cycle if the leader dies mid-sweep. A rare mid-sweep
/// expiry is harmless because the reaper's re-drive is idempotent (chain-token gate), so no
/// keep-alive extension is needed.
/// </para>
/// </remarks>
public sealed class ChainReaperHostedService(
    IServiceScopeFactory scopeFactory,
    IDistributedLockService lockService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<ChainReaperHostedService> logger)
    : BackgroundService
{
    private const string LeaderLockKey = "chain-reaper-leader";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Chain Reaper Worker starting. Sweep interval: {Interval}", SweepInterval);

        // Jittered warm-up so multiple reaper replicas don't sweep in lockstep.
        await Task.Delay(PollingJitter.Startup(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5)), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (executionOptions.Value.EnableChainReaper)
            {
                try
                {
                    await RunLeaderSweepAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occurred during chain reaper sweep");
                }
            }

            try
            {
                await Task.Delay(PollingJitter.Apply(SweepInterval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Chain Reaper Worker stopped");
    }

    /// <summary>
    /// Acquires the singleton <c>chain-reaper-leader</c> lease for this cycle and, only if this
    /// replica wins it, runs the full multi-schema sweep. Replicas that do not win the lease skip
    /// the cycle, so the discovery and per-flow polling happen once across the cluster rather than
    /// once per replica. The lease is released when the sweep completes (or on failure, via the
    /// handle's disposal).
    /// </summary>
    private async Task RunLeaderSweepAsync(CancellationToken stoppingToken)
    {
        var leaseSeconds = Math.Max(30, executionOptions.Value.ChainReaperLeaderLeaseSeconds);

        await using var leadership = await lockService.TryAcquireLockAsync(
            LeaderLockKey, leaseSeconds, stoppingToken);

        if (leadership is null)
        {
            logger.ChainReaperLeadershipHeldElsewhere();
            return;
        }

        logger.ChainReaperLeadershipAcquired(leaseSeconds);
        await SweepAllFlowSchemasAsync(stoppingToken);
    }

    /// <summary>
    /// Discovers every flow key from <c>sys_flows</c> and runs the reaper once per flow schema,
    /// each in its own scope with the schema established. Sweeps up to
    /// <c>WorkflowExecutionOptions.ChainReaperMaxConcurrentSweeps</c> schemas concurrently so the
    /// total wall-clock time scales sub-linearly with the number of flows. A per-flow timeout
    /// (<c>ChainReaperFlowTimeoutSeconds</c>) ensures one slow schema cannot block the others.
    /// One bad schema is logged and skipped so it cannot abort the whole sweep.
    /// </summary>
    private async Task SweepAllFlowSchemasAsync(CancellationToken stoppingToken)
    {
        // 1) Discover all flow keys (own scope; the repository switches to sys_flows internally).
        //    GetActiveFlowKeysAsync calls GetDbSetAsync which requires an active UoW — open a
        //    read-only RequiresNew scope so Aether's DbContextProvider has one available.
        IReadOnlyList<string> flowKeys;
        await using (var discoveryScope = scopeFactory.CreateAsyncScope())
        {
            var uowManager = discoveryScope.ServiceProvider.GetRequiredService<IUnitOfWorkManager>();
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { IsTransactional = false, Scope = UnitOfWorkScopeOption.RequiresNew });
            var instanceRepository = discoveryScope.ServiceProvider.GetRequiredService<IInstanceRepository>();
            flowKeys = await instanceRepository.GetActiveFlowKeysAsync(stoppingToken);
        }

        if (flowKeys.Count == 0)
            return;

        var opts = executionOptions.Value;
        var maxConcurrent = Math.Max(1, opts.ChainReaperMaxConcurrentSweeps);
        var flowTimeout = TimeSpan.FromSeconds(Math.Max(10, opts.ChainReaperFlowTimeoutSeconds));

        // 2) Sweep each flow schema in its own scope, bounded to maxConcurrent in-flight at a time.
        //    SemaphoreSlim gates entry; Task.Run ensures all tasks are issued eagerly so the
        //    semaphore can release slots as earlier schemas finish.
        using var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);

        var tasks = flowKeys.Select(flowKey => Task.Run(async () =>
        {
            await semaphore.WaitAsync(stoppingToken);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                cts.CancelAfter(flowTimeout);

                await using var scope = scopeFactory.CreateAsyncScope();
                var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

                using (currentSchema.Change(flowKey))
                {
                    var reaper = scope.ServiceProvider.GetRequiredService<IChainReaperService>();
                    await reaper.SweepAsync(cts.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.ChainReaperFlowSweepTimedOut(flowKey);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chain reaper sweep failed for flow schema {FlowKey}", flowKey);
            }
            finally
            {
                semaphore.Release();
            }
        }, stoppingToken));

        await Task.WhenAll(tasks);
    }
}
