using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Infrastructure.DataSink;
using BBT.Workflow.Infrastructure.Execution.PostCommit;
using BBT.Workflow.Infrastructure.HostedServices;
using BBT.Workflow.Infrastructure.Security;
using BBT.Workflow.Infrastructure.Scripting;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Remote.Extensions;
using BBT.Workflow.Schemas;
using BBT.Workflow.Security;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up infrastructure services in an <see cref="IServiceCollection" />.
/// </summary>
public static class WorkflowInfrastructureModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds the infrastructure module services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="configuration">The configuration instance for service configuration.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    /// <remarks>
    /// Infrastructure module manages its own dependencies.
    /// If IDistributedCache is not registered, a fallback in-memory cache will be used.
    /// URL template services are configured for HATEOAS support.
    /// </remarks>
    public static IServiceCollection AddInfrastructureModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return AddInfrastructureModuleCore(services, configuration);
    }

    /// <summary>
    /// Adds the infrastructure module services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    /// <remarks>
    /// Infrastructure module manages its own dependencies.
    /// If IDistributedCache is not registered, a fallback in-memory cache will be used.
    /// This overload does not configure URL template services (used for testing).
    /// </remarks>
    public static IServiceCollection AddInfrastructureModule(
        this IServiceCollection services)
    {
        return AddInfrastructureModuleCore(services, configuration: null);
    }

    private static IServiceCollection AddInfrastructureModuleCore(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        services.AddAetherInfrastructure();
        
        // Ensure IDistributedCache is available for SchemaValidator
        // If not registered by Application/API layer, use in-memory fallback
        if (!services.Any(sd => sd.ServiceType == typeof(IDistributedCache)))
        {
            services.AddDistributedMemoryCache();
        }
        
        // URL Template Services for HATEOAS (only if configuration is provided)
        if (configuration != null)
        {
            services.AddUrlTemplateServices(configuration);
        }
        
        // DbContext
        services.AddSingleton<SchemaAwareModelCacheKeyFactory>();
        services.AddScoped<IMultiSchemaMigrator<WorkflowDbContext>, MultiSchemaMigrator<WorkflowDbContext>>();
        
        // Security - Schema Validation
        services.AddScoped<ISchemaValidator, SchemaValidator>();
        
        // You can register your repositories here.
        services.AddScoped<IInstanceRepository, EfCoreInstanceRepository>();
        services.AddScoped<IInstanceCorrelationRepository, EfCoreInstanceCorrelationRepository>();
        services.AddScoped<IInstanceTransitionRepository, EfCoreInstanceTransitionRepository>();
        services.AddScoped<IInstanceTaskRepository, EfCoreInstanceTaskRepository>();
        services.AddScoped<IInstanceJobRepository, EfCoreInstanceJobRepository>();
        
        // Remote vnext api
        services.AddVNextApiServices();
        
        // Instance Gateways - route between local and remote execution
        services.AddInstanceGatewayServices();
        
        // Monitoring
        services.AddSingleton<IWorkflowMetrics, PrometheusWorkflowMetrics>();
        services.AddSingleton<WorkflowDatabaseInterceptor>();
        services.AddSingleton<WorkflowTransactionInterceptor>();
        
        // Hosted Services
        services.AddHostedService<SystemHealthMonitoringHostedService>();
        
        // DataSink Integration (replaces ClickHouse integration)
        services.AddDataSinkServices();
        services.AddClickHouseDataSinks();
        services.RegisterDataSinks();
        
        // Schema Migration Orchestration
        services.AddScoped<ISchemaMigrationOrchestrator, SchemaMigrationOrchestrator>();
        
        return services;
    }

    /// <summary>
    /// Registers runtime-only infrastructure services that require external dependencies:
    /// domain discovery (requires <see cref="BBT.Aether.DistributedCache.IDistributedCacheService"/>),
    /// embedded scripting (requires <see cref="BBT.Workflow.Caching.IComponentCacheStore"/> from Application layer),
    /// and post-commit idempotency store (requires <see cref="BBT.Aether.DistributedCache.IDistributedCacheService"/>).
    /// Call this only from hosts that register <c>AddApplicationModule()</c> and <c>AddDistributedCache()</c>.
    /// Do NOT call from DbMigrator or other minimal hosts.
    /// </summary>
    public static IServiceCollection AddInfrastructureRuntimeServices(this IServiceCollection services)
    {
        // Domain discovery (needs IDistributedCacheService)
        services.AddDomainDiscovery();

        // Post-Commit Idempotency Store (needs IDistributedCacheService)
        services.AddSingleton<IPostCommitIdempotencyStore, DistributedCacheIdempotencyStore>();

        // Embedded Script Services (needs IComponentCacheStore from Application layer)
        services.AddEmbeddedScriptServices();
        services.ConfigureEmbeddedScripts(opt =>
        {
            opt.Add(
                NotificationScriptProvider.DefaultKey,
                "BBT.Workflow.Tasks.Scripting.NotificationMapping.csx",
                typeof(EmbeddedScriptEntry).Assembly);
        });

        return services;
    }

    /// <summary>
    /// Registers workflow event hooks that execute before domain events are published.
    /// These hooks depend on application-layer services (<see cref="BBT.Workflow.Instances.IInstanceCancellationService"/>)
    /// and a configured event bus (<see cref="BBT.Aether.Events.IDistributedEventBus"/>).
    /// Call this only from hosts that register both <c>AddApplicationModule()</c> and an event bus.
    /// Do NOT call from DbMigrator or other minimal hosts.
    /// </summary>
    public static IServiceCollection AddWorkflowEventHooks(this IServiceCollection services)
    {
        services.AddEventHook<InstanceSubCompletedEvent, InstanceSubCompletedEventHook>();
        services.AddEventHook<InstanceSubStateChangedEvent, InstanceSubStateChangedEventHook>();
        services.AddEventHook<InstanceCanceledEvent, InstanceCanceledEventHook>();
        services.AddEventHook<InstanceCompletedCleanupEvent, InstanceCompletedCleanupEventHook>();
        services.AddEventHook<InstanceFaultedCleanupEvent, InstanceFaultedCleanupEventHook>();
        return services;
    }
}