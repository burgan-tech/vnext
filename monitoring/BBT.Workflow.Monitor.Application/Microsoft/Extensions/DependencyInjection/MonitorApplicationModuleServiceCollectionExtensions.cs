using BBT.Workflow.Monitor.Components;
using BBT.Workflow.Monitor.Instances;
using BBT.Workflow.Runtime;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Monitor application-layer services.
/// </summary>
public static class MonitorApplicationModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds monitor-specific application services (instance query, component query).
    /// Also registers <see cref="IRuntimeService"/> so component cache backends and Monitor full-list
    /// resolution can load definitions from PostgreSQL when the in-memory snapshot is cold.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMonitorApplicationModule(this IServiceCollection services)
    {
        services.AddAetherApplication();

        services.AddScoped<IRuntimeService, RuntimeService>();
        services.AddScoped<IMonitorInstanceQueryService, MonitorInstanceQueryService>();
        services.AddScoped<IMonitorComponentQueryService, MonitorComponentQueryService>();

        return services;
    }
}
