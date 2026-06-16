namespace BBT.Workflow.Mcp.Meta;

/// <summary>
/// Warms <see cref="IMetaProvider"/> at startup and retries on an interval until the npm package
/// loads (or the host stops). Runs in the background so a transient npm outage never blocks startup
/// or the live component/runtime tools.
/// </summary>
public sealed class MetaWarmupHostedService(IMetaProvider metaProvider, ILogger<MetaWarmupHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    private const int MaxAttempts = 10;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= MaxAttempts && !stoppingToken.IsCancellationRequested; attempt++)
        {
            if (await metaProvider.LoadAsync(stoppingToken))
                return;

            logger.LogWarning("vnext-meta not loaded (attempt {Attempt}/{Max}); retrying in {Seconds}s.",
                attempt, MaxAttempts, RetryInterval.TotalSeconds);

            try
            {
                await Task.Delay(RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
