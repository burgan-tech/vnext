using BBT.Aether.MultiSchema;
using BBT.Aether.MultiSchema.EntityFrameworkCore.Interceptors;
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
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    /// <remarks>
    /// Infrastructure module manages its own dependencies.
    /// If IDistributedCache is not registered, a fallback in-memory cache will be used.
    /// </remarks>
    public static IServiceCollection AddInfrastructureModule(
        this IServiceCollection services)
    {
        services.AddAetherInfrastructure();
        
        // Ensure IDistributedCache is available for SchemaValidator
        // If not registered by Application/API layer, use in-memory fallback
        if (!services.Any(sd => sd.ServiceType == typeof(IDistributedCache)))
        {
            services.AddDistributedMemoryCache();
        }
        
        // DbContext
        services.AddSingleton<NpgsqlSchemaConnectionInterceptor>();
        services.AddScoped<IMultiSchemaMigrator<WorkflowDbContext>, MultiSchemaMigrator<WorkflowDbContext>>();
        
        // Security - Schema Validation
        services.AddScoped<ISchemaValidator, SchemaValidator>();
        
        // You can register your repositories here.
        services.AddScoped<IInstanceRepository, EfCoreInstanceRepository>();
        services.AddScoped<IInstanceCorrelationRepository, EfCoreInstanceCorrelationRepository>();
        services.AddScoped<IInstanceTransitionRepository, EfCoreInstanceTransitionRepository>();
        services.AddScoped<IInstanceTaskRepository, EfCoreInstanceTaskRepository>();
        services.AddScoped<IInstanceJobRepository, EfCoreInstanceJobRepository>();
        
        // Domain discovery
        services.AddDomainDiscovery();
        
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
        
        // Post-Commit Idempotency Store
        services.AddSingleton<IPostCommitIdempotencyStore, DistributedCacheIdempotencyStore>();
        
        // Event Hooks
        services.AddEventHook<InstanceSubCompletedEvent, InstanceSubCompletedEventHook>();
        services.AddEventHook<InstanceSubStateChangedEvent, InstanceSubStateChangedEventHook>();
        services.AddEventHook<InstanceCanceledEvent, InstanceCanceledEventHook>();
        
        // Embedded Script Services
        services.AddEmbeddedScriptServices();
        services.ConfigureEmbeddedScripts(opt =>
        {
            // Script is embedded in BBT.Workflow.Domain assembly
            opt.Add(
                NotificationScriptProvider.DefaultKey,
                "BBT.Workflow.Tasks.Scripting.NotificationMapping.csx",
                typeof(EmbeddedScriptEntry).Assembly);
        });
        
        return services;
    }
}