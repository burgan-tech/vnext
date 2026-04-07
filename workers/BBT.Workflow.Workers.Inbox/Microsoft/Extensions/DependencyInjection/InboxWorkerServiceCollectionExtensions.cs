using BBT.Workflow.Workers.Inbox.HostedServices;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to the Inbox worker.
/// Composes modular extensions for the event-consuming Inbox host.
/// </summary>
public static class InboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds all services required by the Inbox worker host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWorkerInboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();

        // Core domain/application/infrastructure modules
        services
            .AddDomainModule()
            .AddApplicationModule()
            .AddInfrastructureModule(configuration)
            .AddInfrastructureRuntimeServices()
            .AddResultResilience(configuration);

        // ASP.NET Core, serialization
        services
            .AddWorkflowAspNetCore(configuration)
            .AddWorkflowMapper();

        // Dapr, event bus (direct Aether event bus -- no hook decorator for inbox),
        // domain events, event hooks
        services
            .AddWorkflowDapr()
            .AddAetherEventBus(options =>
            {
                options.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                options.PrefixEnvironmentToTopic = true;
                options.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
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

        // Inbox-specific
        services.AddHostedService<InboxProcessorHostedService>();

        return services;
    }
}