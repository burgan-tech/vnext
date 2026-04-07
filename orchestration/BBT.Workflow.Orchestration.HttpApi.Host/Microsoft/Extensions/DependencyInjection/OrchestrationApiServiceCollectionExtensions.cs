using BBT.Workflow.Caching;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.HostedServices;
using BBT.Workflow.Orchestration.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Orchestration API.
/// Composes modular extensions for the full-stack Orchestration host.
/// </summary>
public static class OrchestrationApiServiceCollectionExtensions
{
    /// <summary>
    /// Adds all services required by the Orchestration API host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOrchestrationApiModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();

        // Core domain/application/infrastructure modules
        services
            .AddDomainModule()
            .AddApplicationModule()
            .AddInfrastructureModule(configuration)
            .AddInfrastructureRuntimeServices()
            .AddResultResilience(configuration);

        // ASP.NET Core, serialization, controllers
        services
            .AddWorkflowAspNetCore(configuration)
            .AddWorkflowMapper();

        // Dapr, event bus, domain events, event hooks
        services
            .AddWorkflowDapr()
            .AddWorkflowEventBus(configuration)
            .AddWorkflowDomainEvents()
            .AddWorkflowEventHooks();

        // Database, caching, locking
        services
            .AddWorkflowDbContext(configuration)
            .AddWorkflowDistributedCache(configuration)
            .AddWorkflowDistributedLock(configuration)
            .AddRedis();

        // Background jobs, telemetry, exception handling
        services
            .AddWorkflowBackgroundJobs()
            .AddWorkflowTelemetry(configuration)
            .AddWorkflowExceptionHandling();

        // Runtime middleware, headers, health checks
        services
            .AddWorkflowRuntimeMiddleware()
            .AddWorkflowHeaderService()
            .AddAppHealthChecks();

        // Orchestration-specific
        services
            .AddFunctionHandlers()
            .AddOrchestrationHostedServices();

        return services;
    }

    private static IServiceCollection AddFunctionHandlers(this IServiceCollection services)
    {
        services.AddScoped<IInstanceFunctionHandler, StateFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, ViewFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, DataFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, SchemaFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, ExtensionsFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, AuthorizeFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, AuthorizationMatrixFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandler, HierarchyFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandlerFactory, InstanceFunctionHandlerFactory>();
        return services;
    }

    private static IServiceCollection AddOrchestrationHostedServices(this IServiceCollection services)
    {
#if DEBUG
        services.AddHostedService<MultiSchemaMigrationHostedService>();
#endif
        services.AddHostedService<CacheCleanupHostedService>();
        services.AddHostedService<CacheInitializationHostedService>();
        services.AddHostedService<DomainDiscoveryInitializationHostedService>();
        return services;
    }
}
