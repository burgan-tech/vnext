using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Hosting;
using BBT.Workflow.Instances;
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
/// </remarks>
public sealed class ChainReaperHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<ChainReaperHostedService> logger)
    : BackgroundService
{
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
                    await SweepAllFlowSchemasAsync(stoppingToken);
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
    /// Discovers every flow key from <c>sys_flows</c> and runs the reaper once per flow schema,
    /// each in its own scope with the schema established. One bad schema (e.g. not yet migrated
    /// with the chain-token columns) is logged and skipped so it cannot abort the whole sweep.
    /// </summary>
    private async Task SweepAllFlowSchemasAsync(CancellationToken stoppingToken)
    {
        // 1) Discover all flow keys (own scope; the repository switches to sys_flows internally).
        IReadOnlyList<string> flowKeys;
        await using (var discoveryScope = scopeFactory.CreateAsyncScope())
        {
            var instanceRepository = discoveryScope.ServiceProvider.GetRequiredService<IInstanceRepository>();
            flowKeys = await instanceRepository.GetActiveFlowKeysAsync(stoppingToken);
        }

        // 2) Sweep each flow schema in its own scope, with the per-flow schema established so the
        //    DI-scoped repositories/DbContext resolve the correct schema for that flow.
        foreach (var flowKey in flowKeys)
        {
            stoppingToken.ThrowIfCancellationRequested();

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

                using (currentSchema.Change(flowKey))
                {
                    var reaper = scope.ServiceProvider.GetRequiredService<IChainReaperService>();
                    await reaper.SweepAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chain reaper sweep failed for flow schema {FlowKey}", flowKey);
            }
        }
    }
}
