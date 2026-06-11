using BBT.Workflow.Data;
using BBT.Workflow.Workers.Inbox.Forwarding;
using BBT.Workflow.Workers.Inbox.HostedServices;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Worker Inbox
/// </summary>
public static class InboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Worker Inbox specific services
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddWorkerInboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddDomainModule()
            .AddApplicationModule()
            .AddInfrastructureModule(configuration)
            .AddAspNetCoreModules(configuration)
            .AddResultResilience(configuration)
            .AddDaprClients()
            .AddAetherEventBus(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
            // NOTE: AddWorkflowEventHooks / AddTransitionLockScope / AddBackgroundJob removed —
            // the Inbox is a thin forwarder: it performs NO domain processing and MUST NOT register
            // the flow.transition job handler (that ran transitions in the Inbox process). Transition
            // jobs are enqueued/executed only in the Orchestration host now.
            .AddDomainEventsInfrastructure()
            .AddInfrastructureRuntimeServices()
            .AddDbContext(configuration)
            .AppMapper()
            .AddTelemetry(configuration)
            .AddDistributedCache(configuration)
            .AddDistributedLock(configuration)
            .AddRedis()
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddHostedServices()
            .AddAppHealthChecks();

        // Inbox = thin forwarder: deliver events to Orchestration via Dapr service invocation.
        // Singleton — depends only on configuration/logger and owns one Dapr-invokable HttpClient.
        services.AddSingleton<IOrchestrationForwarder, DaprOrchestrationForwarder>();

        return services;
    }
    
    private static IServiceCollection AddHostedServices(this IServiceCollection services)
    {
        services.AddHostedService<InboxProcessorHostedService>();
        return services;
    }

}