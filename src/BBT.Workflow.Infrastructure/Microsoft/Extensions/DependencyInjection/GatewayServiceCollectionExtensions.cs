using BBT.Workflow.Gateway;
using BBT.Workflow.Infrastructure.Gateway;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering gateway services in an <see cref="IServiceCollection" />.
/// </summary>
public static class GatewayServiceCollectionExtensions
{
    /// <summary>
    /// Adds the gateway pattern services for instance command and query operations.
    /// Gateways route between local and remote execution based on target domain.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddInstanceGatewayServices(this IServiceCollection services)
    {
        // Local gateways - execute locally with schema context
        services.AddScoped<LocalInstanceCommandGateway>();
        services.AddScoped<LocalInstanceQueryGateway>();

        // Remote gateways - delegate to HTTP clients
        services.AddScoped<RemoteInstanceCommandGateway>();
        services.AddScoped<RemoteInstanceQueryGateway>();

        // Routed gateways - route based on IRuntimeInfoProvider.IsDomainMatch()
        // These are registered as the interface implementations
        services.AddScoped<IInstanceCommandGateway, RoutedInstanceCommandGateway>();
        services.AddScoped<IInstanceQueryGateway, RoutedInstanceQueryGateway>();

        return services;
    }
}

