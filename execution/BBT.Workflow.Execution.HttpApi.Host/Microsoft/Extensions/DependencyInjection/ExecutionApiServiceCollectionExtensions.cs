using BBT.Workflow.Runtime;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Service collection extensions specific to Execution API.
/// The Execution service is independent of the workflow Domain/Application stack.
/// It uses only Aether base layers and execution-specific services.
/// </summary>
public static class ExecutionApiServiceCollectionExtensions
{
    /// <summary>
    /// Adds all services required by the Execution API host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddExecutionApiModule(this IServiceCollection services)
    {
        var configuration = services.GetConfiguration();

        // Aether base layers (no workflow Domain/Application)
        services
            .AddAetherDomain()
            .AddAetherApplication()
            .AddAetherInfrastructure();

        // ASP.NET Core, serialization, controllers
        services
            .AddWorkflowAspNetCore(configuration)
            .AddWorkflowMapper();

        // Dapr, event bus
        services
            .AddWorkflowDapr()
            .AddAetherEventBus(opt =>
            {
                opt.DefaultSource =
                    $"urn:vnext:{configuration.GetValue<string?>("ApplicationName")?.ToLowerInvariant()}";
                opt.PrefixEnvironmentToTopic = true;
                opt.PubSubName = configuration["DAPR_PUBSUB_STORE_NAME"]!;
            });

        // Caching, telemetry, exception handling
        services
            .AddWorkflowDistributedCache(configuration)
            .AddWorkflowDistributedLock(configuration)
            .AddRedis()
            .AddWorkflowTelemetry(configuration)
            .AddWorkflowExceptionHandling();

        // Execution-specific
        services
            .AddExecutionHealthChecks()
            .AddDaprNotification(configuration)
            .AddTaskInvokers(configuration);

        services.AddSingleton<IRuntimeInfoProvider, RuntimeInfoProvider>();

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
