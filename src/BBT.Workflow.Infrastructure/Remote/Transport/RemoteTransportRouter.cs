using System.Diagnostics;
using System.Net.Sockets;
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
    IDiscoveryEndpointFeedback endpointFeedback,
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
    private async Task<HttpResponseMessage> SendCoreAsync(
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

            // This is the ONE place the runtime learns that a resolved endpoint does not answer, and
            // with an expiry-free discovery cache it is the only automatic way a moved domain is
            // corrected. Every Remote* client funnels through here, so the report lives here rather
            // than at the ~35 sites that resolve an endpoint.
            //
            // NOT the caller's token. A connect timeout is one of the failures reported here, and it
            // often arrives with the caller already cancelled — passing that token through would
            // cancel the eviction itself, leaving the bad entry in the shared layer for a whole
            // cooldown window while the cooldown claim had already been spent. Same reasoning as the
            // refresher's `handle.ReleaseAsync(CancellationToken.None)`: this is cleanup that has to
            // finish.
            if (DescribeUnreachable(exception) is { } reason)
            {
                await endpointFeedback.ReportUnreachableAsync(
                    endpoint.Domain, reason, CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>
    /// Describes <paramref name="exception"/> when it means "nothing is listening at this address",
    /// or returns <c>null</c> when it means anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list is deliberately narrow. An HTTP error response never reaches this catch — the
    /// transport returns it — so the shapes here are genuinely about reachability, and the ones left
    /// out are left out on purpose:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>A TLS failure</b> is far more often a certificate problem at the right address than a
    ///     stranger answering on a moved one, and evicting on it would send the registry a lookup
    ///     per failed handshake while fixing nothing.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A broken circuit</b> is a verdict about past failures, not a fresh observation; the
    ///     failures that opened it were already reported individually.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A cancelled request</b> is the caller giving up, and a response-read timeout says
    ///     nothing about the address — something answered.
    ///   </description></item>
    /// </list>
    /// </remarks>
    private static string? DescribeUnreachable(Exception exception)
    {
        if (exception is not HttpRequestException httpException)
            return null;

        // The typed error is the modern signal and covers a DNS failure with no inner SocketException.
        if (httpException.HttpRequestError is HttpRequestError.ConnectionError
            or HttpRequestError.NameResolutionError)
        {
            return httpException.HttpRequestError.ToString();
        }

        // Older shapes, and the Dapr shell's normalization, still arrive as an inner SocketException.
        return httpException.InnerException is SocketException socketException
               && socketException.SocketErrorCode is SocketError.ConnectionRefused
                   or SocketError.HostNotFound
                   or SocketError.HostUnreachable
                   or SocketError.NetworkUnreachable
                   or SocketError.TimedOut
            ? socketException.SocketErrorCode.ToString()
            : null;
    }
}
