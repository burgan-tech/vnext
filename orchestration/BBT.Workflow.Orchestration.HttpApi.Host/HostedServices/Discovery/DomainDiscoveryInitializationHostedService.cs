using BBT.Workflow.Discovery;

namespace BBT.Workflow.HostedServices;

/// <summary>
/// Background service that handles domain registration and bulk cache initialization at startup.
/// Registers the current domain with service discovery, then fetches and caches all active domains.
/// Any failure during domain discovery initialization is considered critical and will abort application startup.
/// </summary>
public sealed class DomainDiscoveryInitializationHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<DomainDiscoveryInitializationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation("Starting domain discovery initialization...");
            
            await using var scope = scopeFactory.CreateAsyncScope();
            var registrationService = scope.ServiceProvider.GetRequiredService<IDomainRegistrationService>();
            var discoveryResolver = scope.ServiceProvider.GetRequiredService<IDomainDiscoveryResolver>();
            
            // Step 1: Register this domain
            // await registrationService.RegisterDomainAsync(stoppingToken);
            
            // Step 2: Fetch and cache all domains
            await discoveryResolver.RefreshBulkCacheAsync(stoppingToken);
            
            logger.LogInformation("Domain discovery initialization completed successfully");
        }
        catch (Exception ex)
        {
            // All domain discovery initialization failures are critical and abort application startup
            logger.LogCritical(ex, "Domain discovery initialization failed. Application startup will be aborted.");
            throw;
        }
    }
}
