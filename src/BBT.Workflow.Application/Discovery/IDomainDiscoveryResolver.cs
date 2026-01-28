using BBT.Aether.Results;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Service responsible for resolving domain endpoints from service discovery.
/// Provides caching mechanisms for endpoint resolution with ETag validation.
/// </summary>
public interface IDomainDiscoveryResolver
{
    /// <summary>
    /// Resolves the endpoint for a domain from service discovery cache.
    /// Performs ETag validation on cached entries.
    /// If domain not found in cache, queries registry directly.
    /// </summary>
    /// <param name="domain">The domain name to resolve.</param>
    /// <param name="preferredKind">The preferred endpoint kind (URL or Dapr). Default is URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing endpoint if found, or error if domain does not exist.</returns>
    Task<Result<DiscoveryEndpoint>> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the bulk domain cache from service discovery.
    /// Fetches all active domains with pagination support.
    /// Uses distributed lock to prevent concurrent updates from multiple pods.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RefreshBulkCacheAsync(CancellationToken cancellationToken = default);
}

