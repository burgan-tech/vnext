using BBT.Workflow.Data;
using BBT.Workflow.Workers.Outbox.HostedServices;
using Dapr.Jobs.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to the Outbox worker.
/// Composes modular extensions for the event-publishing Outbox host.
/// </summary>
public static class OutboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds all services required by the Outbox worker host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWorkerOutboxModule(this IServiceCollection services)
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

        // Dapr, event bus, domain events, event hooks
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

        // Outbox-specific background jobs (uses raw AetherBackgroundJob without workflow handlers)
        services
            .AddAetherBackgroundJob<WorkflowDbContext>()
            .AddDaprJobScheduler();

        // Telemetry, exception handling
        services
            .AddWorkflowTelemetry(configuration)
            .AddWorkflowExceptionHandling();

        // Runtime middleware, headers, health checks
        services
            .AddWorkflowRuntimeMiddleware()
            .AddWorkflowHeaderService()
            .AddAppHealthChecks();

        // Outbox-specific
        services.AddHostedService<OutboxProcessorHostedService>();

        return services;
    }
}