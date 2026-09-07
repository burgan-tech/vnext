using BBT.Workflow.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Remote;

/// <summary>
/// The <see cref="IRemoteTransport{TClient}"/> the <c>Remote*</c> services actually receive:
/// dispatches each call to the HTTP or the Dapr shell according to
/// <see cref="DiscoveryEndpoint.Kind"/>.
/// </summary>
/// <remarks>
/// <para>
/// The routing signal is the endpoint's <see cref="EndpointKind"/> — the value the discovery
/// provider decided — and nothing else. There is no second switch that could disagree with the
/// provider, and no URI-shape sniffing. A <c>DomainOverrides</c> entry of <c>url</c> produces
/// <see cref="EndpointKind.Url"/> and lands on HTTP; that is how a single domain is rolled forward
/// or back without touching the global provider.
/// </para>
/// <para>
/// The Dapr shell is resolved lazily and may be absent (a test host that registered no shell).
/// A Dapr-kind endpoint arriving there is a configuration error surfaced as
/// <see cref="HttpRequestException"/> so the callers' transient-error contract still holds. In
/// production the shell is always registered by <c>AddRemoteService</c> and needs no
/// <c>DaprClient</c>: it builds on <c>DaprClient.CreateInvokeHttpClient()</c>, so a missing sidecar
/// shows up as a connection failure — also an <see cref="HttpRequestException"/>.
/// </para>
/// </remarks>
public sealed class RemoteTransportRouter<TClient>(
    HttpRemoteTransport<TClient> http,
    IServiceProvider serviceProvider) : IRemoteTransport<TClient>
    where TClient : class
{
    private readonly Lazy<IDaprRemoteTransport<TClient>?> _dapr =
        new(serviceProvider.GetService<IDaprRemoteTransport<TClient>>);

    /// <inheritdoc />
    public Task<HttpResponseMessage> SendAsync(
        DiscoveryEndpoint endpoint,
        HttpMethod method,
        string relativePath,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (endpoint.Kind != EndpointKind.Dapr)
        {
            return http.SendAsync(endpoint, method, relativePath, configure, cancellationToken);
        }

        var dapr = _dapr.Value
            ?? throw new HttpRequestException(
                $"Endpoint for '{endpoint.DaprAppId ?? endpoint.BaseUrl.Host}' requires Dapr service " +
                $"invocation, but no Dapr transport is registered for {typeof(TClient).Name}. " +
                "Register it through AddRemoteService or set ServiceDiscovery:Provider to 'http'.");

        return dapr.SendAsync(endpoint, method, relativePath, configure, cancellationToken);
    }
}
