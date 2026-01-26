using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Execution API.
/// The Execution service is now completely independent of Domain.
/// </summary>
public static class ExecutionApiServiceCollectionExtensions
{
    /// <summary>
    /// Adds Execution API specific services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddExecutionApiModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();
        services
            .AddAetherDomain()
            .AddAetherApplication()
            .AddAetherInfrastructure()
            .AddAspNetCoreModules(configuration)
            .AddDaprClients()
            .AddAetherEventBus(opt =>
            {
                opt.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                opt.PrefixEnvironmentToTopic = true;
                opt.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            })
            .AppMapper()
            .AddTelemetry(configuration)
            .AddDistributedCache(configuration)
            .AddDistributedLock(configuration)
            .AddRedis()
            .AddExceptionHandling()
            .AddExecutionHealthChecks()
            .AddDaprNotification(configuration)
            .AddTaskInvokers(configuration);
        return services;
    }
    
    private static IServiceCollection AddExecutionHealthChecks(this IServiceCollection services)
    {
        var healthChecksBuilder = services
            .AddHealthChecks()
            .ForwardToPrometheus();
            
        // Add standard health checks for Workflow APIs
        healthChecksBuilder
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]); // Self health check
        
        return services;
    }
}
