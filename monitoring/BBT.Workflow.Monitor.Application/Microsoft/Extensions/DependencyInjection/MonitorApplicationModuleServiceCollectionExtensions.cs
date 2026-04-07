using BBT.Workflow.Monitor.Components;
using BBT.Workflow.Monitor.Instances;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Monitor application-layer services.
/// </summary>
public static class MonitorApplicationModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds monitor-specific application services (instance query, component query).
    /// Call this from the Monitor API host after the core Domain and Infrastructure modules are registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddMonitorApplicationModule(this IServiceCollection services)
    {
        services.AddAetherApplication();

        services.AddScoped<IMonitorInstanceQueryService, MonitorInstanceQueryService>();
        services.AddScoped<IMonitorComponentQueryService, MonitorComponentQueryService>();

        return services;
    }
}
