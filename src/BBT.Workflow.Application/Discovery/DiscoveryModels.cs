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
public sealed record DiscoveryEndpoint(
    EndpointKind Kind,
    Uri BaseUrl,
    string? DaprAppId = null);
