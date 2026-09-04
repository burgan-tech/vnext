using BBT.Workflow.Discovery;

namespace BBT.Workflow.Remote;

/// <summary>
/// The transport shell behind a remote typed client: turns a resolved endpoint plus a logical
/// request into an <see cref="HttpResponseMessage"/>, over whatever wire the endpoint calls for.
/// </summary>
/// <typeparam name="TClient">
/// The remote client interface this transport serves. Mirrors the typed-<c>HttpClient</c>
/// convention so resilience profile and circuit-breaker state stay per client.
/// </typeparam>
/// <remarks>
/// <para>
/// The <c>Remote*</c> app services know nothing about HTTP vs. Dapr: they resolve an endpoint,
/// build a relative path from <c>InstanceUrlTemplates</c>, and hand both to this shell together
/// with a <c>configure</c> callback that sets content and merges headers. The shell owns request
/// construction — which is what lets the Dapr implementation use
/// <c>DaprClient.CreateInvokeMethodRequest</c> (the SDK builds its own message) rather than
/// rewriting one built elsewhere.
/// </para>
/// <para>
/// <c>configure</c> may run more than once: a retrying transport builds a fresh message per
/// attempt (a sent <see cref="HttpRequestMessage"/> cannot be sent again), so the callback must
/// only populate the message it is given and carry no side effects of its own.
/// </para>
/// </remarks>
public interface IRemoteTransport<TClient> where TClient : class
{
    /// <summary>
    /// Sends <paramref name="method"/> <paramref name="relativePath"/> to <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="endpoint">Resolved target. <see cref="DiscoveryEndpoint.Kind"/> selects the wire.</param>
    /// <param name="method">HTTP method.</param>
    /// <param name="relativePath">
    /// Path relative to the endpoint, as produced by <c>InstanceUrlTemplates</c>, with an optional
    /// query string. A leading slash is tolerated.
    /// </param>
    /// <param name="configure">
    /// Populates the outgoing message — content, headers. May be invoked once per attempt.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The raw response; status-code mapping stays with the caller.</returns>
    /// <exception cref="HttpRequestException">
    /// The target could not be reached. Every transport normalizes its own failure shapes to this
    /// so the callers' <c>Error.Transient("remote_network_error", …)</c> contract holds.
    /// </exception>
    Task<HttpResponseMessage> SendAsync(
        DiscoveryEndpoint endpoint,
        HttpMethod method,
        string relativePath,
        Action<HttpRequestMessage>? configure,
        CancellationToken cancellationToken);
}

/// <summary>
/// Marker for the Dapr implementation of <see cref="IRemoteTransport{TClient}"/>, so the router
/// can resolve it lazily and tolerate hosts that register no Dapr transport at all.
/// </summary>
public interface IDaprRemoteTransport<TClient> : IRemoteTransport<TClient> where TClient : class;
