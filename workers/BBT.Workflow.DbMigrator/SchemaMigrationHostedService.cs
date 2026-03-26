using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.DbMigrator;

public sealed class SchemaMigrationHostedService(
    SchemaMigrationRunner runner,
    DaprClient daprClient,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<SchemaMigrationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await runner.RunAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Migration failed unexpectedly");
        }
        finally
        {
            if (!runner.Success)
                Environment.ExitCode = 1;

            await FlushAndShutdownAsync();
        }
    }

    private async Task FlushAndShutdownAsync()
    {
        var flushDelaySeconds = configuration.GetValue("DbMigrator:LogFlushDelaySeconds", 2);
        if (flushDelaySeconds > 0)
            await Task.Delay(TimeSpan.FromSeconds(flushDelaySeconds));

        try
        {
            await daprClient.ShutdownSidecarAsync();
            logger.LogInformation("Dapr sidecar shutdown requested");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to shut down Dapr sidecar — continuing with host stop");
        }

        lifetime.StopApplication();
    }
}
