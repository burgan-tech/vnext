using BBT.Workflow.Caching;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.HostedServices;
using BBT.Workflow.Orchestration.Services;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Orchestration API
/// </summary>
public static class OrchestrationApiServiceCollectionExtensions
{
    /// <summary>
    /// Adds Orchestration API specific services
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddOrchestrationApiModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        
        services
            .AddFunctionHandlers()
            .AddDomainModule()
            .AddApplicationModule()
            .AddInfrastructureModule(configuration) // Infrastructure manages its own dependencies including URL templates
            .AddAspNetCoreModules(configuration)
            .AddResultResilience(configuration)
            .AddDaprClients()
            .AddEventBus(configuration)
            .AddDbContext(configuration)
            .AppMapper()
            .AddTelemetry(configuration)
            .AddDistributedCache(configuration) // Can be called before or after InfrastructureModule
            .AddDistributedLock(configuration)
            .AddBackgroundJob()
            .AddRedis()
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddHostedServices()
            .AddAppHealthChecks();
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

    private static IServiceCollection AddHostedServices(this IServiceCollection services)
    {
        // Add any Orchestration-specific hosted services
        #if DEBUG
        services.AddHostedService<MultiSchemaMigrationHostedService>();
        #endif
        services.AddHostedService<CacheCleanupHostedService>();
        services.AddHostedService<CacheInitializationHostedService>();
        services.AddHostedService<DomainDiscoveryInitializationHostedService>();
        return services;
    }
}
