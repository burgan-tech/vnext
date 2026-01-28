
namespace BBT.Workflow.Caching;

/// <summary>
/// Background service responsible for initializing cache components during application startup.
/// This service delegates the actual initialization work to <see cref="IRuntimeCacheInitializer"/>,
/// which loads workflow definitions, tasks, functions, views, schemas, and extensions
/// into the domain cache context for improved runtime performance.
/// </summary>
public class CacheInitializationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CacheInitializationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting cache initialization...");
            
            await using var scope = scopeFactory.CreateAsyncScope();
            var initializer = scope.ServiceProvider.GetRequiredService<IRuntimeCacheInitializer>();
            await initializer.InitializeAsync(stoppingToken);
            
            logger.LogInformation("Cache initialization completed successfully");
        }
        catch (Exception ex)
        {
            // Other exceptions during cache initialization are logged but don't crash the application
            logger.LogError(ex, "Error during cache initialization. Application will continue with empty cache.");
        }
    }
}