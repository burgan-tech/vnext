using System.Diagnostics;
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

        // The guards stay in this synchronous body on purpose. Both of them are contract throws the
        // callers rely on being synchronous, and the span belongs around the call, not around the
        // argument checks.
        if (endpoint.Kind != EndpointKind.Dapr)
        {
            return SendCoreAsync(http, "http", endpoint, method, relativePath, configure, cancellationToken);
        }

        var dapr = _dapr.Value
            ?? throw new HttpRequestException(
                $"Endpoint for '{endpoint.DaprAppId ?? endpoint.BaseUrl.Host}' requires Dapr service " +
                $"invocation, but no Dapr transport is registered for {typeof(TClient).Name}. " +
                "Register it through AddRemoteService or set ServiceDiscovery:Provider to 'http'.");

        return SendCoreAsync(dapr, "dapr", endpoint, method, relativePath, configure, cancellationToken);
    }

    /// <summary>
    /// Wraps the outbound call in its span.
    /// <para>
    /// This method is <c>async</c> for a load-bearing reason. The shells below <c>return</c> the
    /// retry policy's task rather than awaiting it, so a <c>using</c> span in a non-async method
    /// would be disposed before the first attempt even ran: it would report roughly zero
    /// milliseconds, mis-parent attempts 2..N, and never set the status it exists to carry. Awaiting
    /// here is what makes the span cover the whole logical call — retries, circuit-breaker waits and
    /// the sidecar-failure normalization included. The cost is one state machine against a network
    /// round trip.
    /// </para>
    /// </summary>
    private static async Task<HttpResponseMessage> SendCoreAsync(
        IRemoteTransport<TClient> transport,
        string transportName,
        DiscoveryEndpoint endpoint,
        HttpMethod method,
        string relativePath,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken)
    {
        using var activity = GatewayActivityHelper.StartSend(
            typeof(TClient).Name, transportName, endpoint);

        try
        {
            return await transport.SendAsync(endpoint, method, relativePath, configure, cancellationToken);
        }
        catch (Exception exception)
        {
            // A breaker opening, a pessimistic timeout and a sidecar ERR_DIRECT_INVOKE all surface
            // here and nowhere else in the trace.
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
    }
}
