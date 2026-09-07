using BBT.Workflow.Gateway;
using BBT.Workflow.Scripting.Related;

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

        services.AddScoped<LocalInstanceRetryGateway>();
        services.AddScoped<RemoteInstanceRetryGateway>();

        services.AddScoped<LocalAuthorizeGateway>();
        services.AddScoped<RemoteAuthorizeGateway>();

        services.AddKeyedScoped<IRelatedInstanceReader, LocalRelatedInstanceReader>(RelatedReaderKeys.Local);

        // RemoteRelatedInstanceReader sends through the IRemoteTransport shell, so it must come from
        // the AddRemoteService registration to inherit the timeout / retry / circuit-breaker stack
        // (and the HTTP-vs-Dapr routing) its siblings get. AddKeyedScoped<,> alone would construct it
        // without a registered shell — and, before the shell existed, handed it a default HttpClient
        // with a 100s timeout: on the synchronous transition pipeline, with batch groups awaited
        // sequentially, one hung domain would stall a transition for 100s per group. This keyed
        // entry is an alias onto that registration, not a second construction path.
        services.AddKeyedScoped<IRelatedInstanceReader>(
            RelatedReaderKeys.Remote,
            (serviceProvider, _) => serviceProvider.GetRequiredService<RemoteRelatedInstanceReader>());

        // Routed gateways - route based on IRuntimeInfoProvider.IsDomainMatch()
        // These are registered as the interface implementations
        services.AddScoped<IInstanceCommandGateway, RoutedInstanceCommandGateway>();
        services.AddScoped<IInstanceRetryGateway, RoutedInstanceRetryGateway>();
        services.AddScoped<IInstanceQueryGateway, RoutedInstanceQueryGateway>();
        services.AddScoped<IAuthorizeGateway, RoutedAuthorizeGateway>();
        services.AddScoped<IRelatedInstanceReader, RoutedRelatedInstanceReader>();

        return services;
    }
}

