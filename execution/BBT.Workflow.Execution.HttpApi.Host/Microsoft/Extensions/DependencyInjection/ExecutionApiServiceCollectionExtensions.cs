using BBT.Workflow.Execution.Invocation;
using BBT.Workflow.Runtime;
using BBT.Workflow.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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

        services.AddSingleton<IRuntimeInfoProvider, RuntimeInfoProvider>();
        services.AddScoped<TaskInvokeHandler>();

        services.AddGrpc(options =>
        {
            // Aligned with the sidecar's http-max-request-size: "64" (MB). Envelopes no longer
            // carry the instance data (dead-weight removal from TaskTraceContext), so typical
            // payloads are small; the headroom stays for large authored bindings and large
            // downstream responses on the send side.
            options.MaxReceiveMessageSize = 64 * 1024 * 1024;
            options.MaxSendMessageSize = 64 * 1024 * 1024;
        });

        return services;
    }
    
    private static IServiceCollection AddExecutionHealthChecks(this IServiceCollection services)
    {
        var healthChecksBuilder = services
            .AddHealthChecks();
            
        // Add standard health checks for Workflow APIs
        healthChecksBuilder
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck<PythonRuntimeHealthCheck>("python", tags: ["ready"]); // Self health check
        
        return services;
    }
}
