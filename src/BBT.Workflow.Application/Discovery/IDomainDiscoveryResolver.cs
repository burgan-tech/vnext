using BBT.Aether.Results;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Service responsible for resolving domain endpoints from service discovery.
/// Every resolution queries the registry directly - there is no cache, so discovery is
/// authoritative and a moved or de-registered endpoint is never masked behind a stale entry.
/// </summary>
public interface IDomainDiscoveryResolver
{
    /// <summary>
    /// Resolves the endpoint for a domain by querying the service discovery registry directly.
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

