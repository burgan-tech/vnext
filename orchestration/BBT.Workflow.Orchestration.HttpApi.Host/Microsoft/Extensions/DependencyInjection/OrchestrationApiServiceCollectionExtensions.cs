using BBT.Workflow.Caching;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.HostedServices;
using BBT.Workflow.HttpApi.Shared.HealthChecks;
using BBT.Workflow.Orchestration.Services;
using HealthChecks.NpgSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

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
            .AddWorkflowEventHooks()
            .AddDomainEventsInfrastructure()
            .AddInfrastructureRuntimeServices()
            .AddDbContext(configuration)
            .AppMapper()
            .AddTelemetry(configuration)
            .AddDistributedCache(configuration) // Can be called before or after InfrastructureModule
            .AddDistributedLock(configuration)
            .AddTransitionLockScope()
            .AddBackgroundJob()
            .AddRedis()
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddHostedServices()
            .AddAppHealthChecks()
            .AddOrchestrationDbHealthCheck(configuration);
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
        services.AddScoped<IInstanceFunctionHandler, MasterFunctionHandler>();
        services.AddScoped<IInstanceFunctionHandlerFactory, InstanceFunctionHandlerFactory>();

        services.AddScoped<IDomainFunctionHandler, HumanTaskFunctionHandler>();
        services.AddScoped<IDomainFunctionHandler, DefaultDomainFunctionHandler>();
        services.AddScoped<IDomainFunctionHandlerFactory, DomainFunctionHandlerFactory>();
        return services;
    }

    private static IServiceCollection AddOrchestrationDbHealthCheck(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HealthCheckCacheOptions>(
            configuration.GetSection(HealthCheckCacheOptions.SectionName));

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "Connection string 'Default' is required for the database health check.");

        // Singleton: TTL state + SemaphoreSlim must survive across probes.
        services.TryAddSingleton<CachedHealthCheck>(sp =>
        {
            var ttl = sp.GetRequiredService<IOptions<HealthCheckCacheOptions>>().Value.Ttl;
            IHealthCheck inner = new NpgSqlHealthCheck(new NpgSqlHealthCheckOptions(connectionString));
            return new CachedHealthCheck(inner, ttl, TimeProvider.System);
        });

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            name: "database",
            factory: sp => sp.GetRequiredService<CachedHealthCheck>(),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"],
            timeout: TimeSpan.FromSeconds(2)));

        return services;
    }

    private static IServiceCollection AddHostedServices(this IServiceCollection services)
    {
        // Add any Orchestration-specific hosted services
        #if DEBUG
        services.AddHostedService<MultiSchemaMigrationHostedService>();
        #endif
        services.AddHostedService<DomainDiscoveryInitializationHostedService>();
        services.AddHostedService<ChainReaperHostedService>();
        return services;
    }
}
