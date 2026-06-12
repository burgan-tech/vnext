using BBT.Aether.Events;
using BBT.Workflow.Hosting;

namespace BBT.Workflow.Workers.Outbox.HostedServices;

public sealed class OutboxProcessorHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxProcessorHostedService> logger,
    AetherOutboxOptions options)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Outbox Processor Worker starting. Processing interval: {Interval}",
            options.ProcessingInterval);

        // Brief warm-up to let the sidecar/other services initialize, then begin polling.
        // Jittered so scaled replicas don't start (and stay) in lockstep.
        await Task.Delay(PollingJitter.Startup(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IOutboxProcessor>();
                await processor.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                logger.LogInformation("Outbox processing cancelled");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error occurred during outbox processing cycle");
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

        logger.LogInformation("Outbox Processor Worker stopped");
    }
}