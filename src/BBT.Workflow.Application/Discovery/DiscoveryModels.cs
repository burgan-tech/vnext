namespace BBT.Workflow.Discovery;

/// <summary>
/// Specifies the type of endpoint for domain communication.
/// </summary>
public enum EndpointKind
{
    /// <summary>
    /// Standard HTTP/HTTPS URL endpoint.
    /// </summary>
    Url,

    /// <summary>
    /// Dapr service invocation endpoint using app ID.
    /// </summary>
    Dapr
}

/// <summary>
/// Represents a resolved endpoint for a domain from service discovery.
/// </summary>
/// <param name="Kind">The type of endpoint (URL or Dapr).</param>
/// <param name="BaseUrl">The base URL for HTTP requests.</param>
/// <param name="DaprAppId">The Dapr application ID for service invocation (optional, only for Dapr kind).</param>
/// <param name="Domain">
/// The domain this endpoint was resolved for. Optional only because a hand-built endpoint in a test
/// has no reason to carry one; every provider fills it.
/// </param>
/// <remarks>
/// <b><see cref="Domain"/> is what makes a transport failure actionable.</b> The record used to carry
/// only where to send and how, which left the one piece of information needed to answer "whose cached
/// address just failed?" outside it — a socket error names a host, and a host cannot be mapped back to
/// a cache entry. Carrying the domain is what lets
/// <c>IDiscoveryEndpointFeedback</c> evict the entry that produced a dead address. It is also, not
/// incidentally, the value worth having in the span.
/// </remarks>
public sealed record DiscoveryEndpoint(
    EndpointKind Kind,
    Uri BaseUrl,
    string? DaprAppId = null,
    string? Domain = null);
