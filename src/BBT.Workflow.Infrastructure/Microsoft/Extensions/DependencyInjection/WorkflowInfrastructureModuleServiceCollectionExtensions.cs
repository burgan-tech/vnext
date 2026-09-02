using BBT.Aether.MultiSchema;
using BBT.Workflow.Caching;
using BBT.Workflow.Data;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Infrastructure.DataSink;
using BBT.Workflow.Infrastructure.Execution.PostCommit;
using BBT.Workflow.Infrastructure.Security;
using BBT.Workflow.Infrastructure.Scripting;
using BBT.Workflow.Instances;
using BBT.Workflow.Remote.Extensions;
using BBT.Workflow.Schemas;
using BBT.Workflow.Security;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BBT.Workflow.Authorization.Extensions;

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
        
        // Explicit InstanceData persist path (per-instance FOR UPDATE lock + versioning).
        services.AddScoped<IInstanceDataWriteService, InstanceDataWriteService>();

        // You can register your repositories here.
        services.AddScoped<IInstanceRepository, EfCoreInstanceRepository>();
        services.AddScoped<IInstanceCorrelationRepository, EfCoreInstanceCorrelationRepository>();
        services.AddScoped<IInstanceTransitionRepository, EfCoreInstanceTransitionRepository>();
        services.AddScoped<IInstanceTaskRepository, EfCoreInstanceTaskRepository>();
        services.AddScoped<IInstanceJobRepository, EfCoreInstanceJobRepository>();
        services.AddScoped<IInstanceActionRepository, EfCoreInstanceActionRepository>();

        // Named HTTP clients for the external HTTP task executor (issue #399) — concrete
        // transport, so it lives here rather than in the Application layer that consumes it.
        services.AddExternalHttpTaskClients();

        // Remote vnext api
        services.AddVNextApiServices();
        services.AddCallerRoleResolver(services.GetConfiguration());
        
        // Instance Gateways - route between local and remote execution
        services.AddInstanceGatewayServices();
        
        // DataSink Integration (no sinks are registered by default; concrete sinks plug in here)
        services.AddDataSinkServices();
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

        // Notification channel resolver (needed by NotificationTaskExecutor in Application layer)
        services.AddNotificationChannelResolver();

        return services;
    }

    /// <summary>
    /// Registers the named HTTP clients the external HTTP task executor
    /// (<c>BBT.Workflow.Tasks.Executors.ExternalHttpTaskInvoker</c>) sends through. Mirrors the
    /// Execution host's <c>AddWorkflowHttpClient</c> (same client names, decompression, connection
    /// cap, cookie policy and SSL-bypass variant) so a task behaves identically whichever host
    /// performs the call. The 30s base timeout is overridden per request from the task's
    /// <c>timeoutSeconds</c> by the shared <c>HttpTaskInvocation</c> core.
    /// </summary>
    private static IServiceCollection AddExternalHttpTaskClients(this IServiceCollection services)
    {
        // Default HTTP client with SSL validation enabled
        services.AddHttpClient(BBT.Workflow.Execution.WorkflowHttpClientNames.Default, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = int.MaxValue;
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 10,
                UseCookies = false
            });

        // HTTP client with SSL validation disabled (validateSsl: false tasks)
        services.AddHttpClient(BBT.Workflow.Execution.WorkflowHttpClientNames.NoSslValidation, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = int.MaxValue;
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 10,
                UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

        return services;
    }
}
