using BBT.Workflow.Data;
using BBT.Workflow.Workers.Outbox.HostedServices;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Worker Outbox
/// </summary>
public static class OutboxWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Worker Outbox specific services
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddWorkerOutboxModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddDomainModule()
            .AddApplicationModule()
            .AddInfrastructureModule()
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
            .AddDbContext(configuration)
            .AppMapper()
            .AddTelemetry(configuration)
            .AddDistributedCache(configuration)
            .AddDistributedLock(configuration)
            .AddAetherBackgroundJob<WorkflowDbContext>()
            .AddDaprJobScheduler()
            .AddRedis()
            .AddExceptionHandling()
            .AddRuntimeMiddleware()
            .AddHeaderService()
            .AddHostedServices()
            .AddAppHealthChecks();
        return services;
    }
    private static IServiceCollection AddHostedServices(this IServiceCollection services)
    {
        services.AddHostedService<OutboxProcessorHostedService>();
        return services;
    }
}