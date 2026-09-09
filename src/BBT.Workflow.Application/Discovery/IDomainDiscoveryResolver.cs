using BBT.Aether.Results;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Service responsible for resolving domain endpoints from service discovery.
/// </summary>
/// <remarks>
/// Whether a resolution reaches the registry depends on the provider and on
/// <c>ServiceDiscovery:Cache</c>. With the cache off — still the code default — every resolution
/// queries the registry directly, so a moved or de-registered endpoint is never masked. With it on
/// (default provider only) a registration may be served from cache for a bounded age, refreshed in
/// bulk on a fixed window, and forcible through <c>POST utilities/discovery/refresh</c>.
/// A failed resolution is never cached under either setting.
/// </remarks>
public interface IDomainDiscoveryResolver
{
    /// <summary>
    /// Resolves the endpoint for a domain, from the discovery cache when one is configured and
    /// fresh, and from the registry otherwise.
    /// </summary>
    /// <param name="domain">The domain name to resolve.</param>
    /// <param name="preferredKind">The preferred endpoint kind (URL or Dapr). Default is URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing endpoint if found, or error if domain does not exist.</returns>
    Task<Result<DiscoveryEndpoint>> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default);
}

