using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Recovery;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Workers.Outbox.HostedServices;

/// <summary>
/// Periodically sweeps for stuck-Busy auto-chains (instances Busy with an active chain token,
/// a stale heartbeat and no live job) and resolves them via <see cref="IChainReaperService"/>.
/// Gated by <c>WorkflowExecutionOptions.EnableChainReaper</c>.
/// </summary>
/// <remarks>
/// Draft (S7) — not compiled. Multi-schema gap: this sweeps the worker's current schema/domain
/// scope only; a full deployment must iterate tenant schemas (e.g. resolve schemas and run a
/// sweep per schema scope). Interval is fixed here; promote to options if needed.
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

        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (executionOptions.Value.EnableChainReaper)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reaper = scope.ServiceProvider.GetRequiredService<IChainReaperService>();
                    await reaper.SweepAsync(stoppingToken);
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
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Chain Reaper Worker stopped");
    }
}
