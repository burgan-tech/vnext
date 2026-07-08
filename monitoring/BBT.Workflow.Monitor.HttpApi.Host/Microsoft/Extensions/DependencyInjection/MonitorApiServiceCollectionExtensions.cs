using BBT.Workflow.Monitor.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to the Monitor API host.
/// Composes modular extensions for a read-only monitoring API.
/// </summary>
public static class MonitorApiServiceCollectionExtensions
{
    /// <summary>
    /// Registers all services required by the Monitor API.
    /// This is a read-only API; event bus, outbox, inbox, background jobs,
    /// and the full application pipeline are intentionally excluded.
    /// Only the cache module from Application is used for component resolution.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMonitorApiModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();

        // Core modules: Domain + Infrastructure (no full Application pipeline)
        services
            .AddDomainModule()
            .AddInfrastructureModule(configuration);

        // Application cache module only (IComponentCacheStore, cache backends, cast handlers)
        services
            .AddAetherApplication()
            .AddApplicationCacheModule()
            .AddMonitorApplicationModule();

        // ASP.NET Core, serialization, controllers
        services
            .AddAspNetCoreModules(configuration)
            .AppMapper();

        // Dapr client: required by AddDistributedCache (Dapr-backed cache)
        services.AddDaprClients();

        // Database, caching, locking, telemetry
        services
            .AddDbContext(configuration)
            .AddDistributedCache(configuration)
            .AddDistributedLock(configuration)
            .AddTelemetry(configuration);

        // Exception handling, runtime, headers
        services
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService();

        // Health checks
        services
            .AddAppHealthChecks()
            .AddMonitorHealthChecks();

        return services;
    }

    /// <summary>
    /// Adds Monitor-specific health checks for Redis and the vNext Orchestration API.
    /// These supplement the base PostgreSQL and self checks registered by <c>AddAppHealthChecks</c>.
    /// </summary>
    private static IServiceCollection AddMonitorHealthChecks(this IServiceCollection services)
    {
        services.AddHttpClient("orchestrator-health")
            .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(5));

        services
            .AddHealthChecks()
            .AddCheck<MonitorDistributedCacheHealthCheck>("redis", tags: ["ready"])
            .AddCheck<MonitorOrchestratorHealthCheck>("orchestrator", tags: ["ready"]);

        return services;
    }
}
