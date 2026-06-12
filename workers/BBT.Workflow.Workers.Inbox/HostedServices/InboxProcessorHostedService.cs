using BBT.Aether.Events;
using BBT.Workflow.Hosting;

namespace BBT.Workflow.Workers.Inbox.HostedServices;

public sealed class InboxProcessorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<InboxProcessorHostedService> logger,
    AetherOutboxOptions options)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Inbox Processor Worker starting. Processing interval: {Interval}",
            options.ProcessingInterval);

        // Brief warm-up to let the sidecar/other services initialize, then begin polling.
        // Jittered so scaled replicas don't start (and stay) in lockstep.
        await Task.Delay(PollingJitter.Startup(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IInboxProcessor>();
                await processor.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                logger.LogInformation("Inbox cancelled");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during inbox cycle");
                // Continue processing in next iteration
            }

            try
            {
                await Task.Delay(PollingJitter.Apply(options.ProcessingInterval), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                break;
            }
        }

        logger.LogInformation("Inbox Processor Worker stopped");
    }
}