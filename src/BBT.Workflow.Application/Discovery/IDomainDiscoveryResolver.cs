namespace BBT.Workflow.Discovery;

/// <summary>
/// Service responsible for resolving domain endpoints from service discovery.
/// Provides caching and fallback mechanisms for endpoint resolution.
/// </summary>
public interface IDomainDiscoveryResolver
{
    /// <summary>
    /// Resolves the endpoint for a given domain.
    /// Uses distributed cache with fallback to Discovery API.
    /// When service discovery is disabled, returns the static BaseUrl from configuration.
    /// </summary>
    /// <param name="domain">The domain name to resolve.</param>
    /// <param name="preferredKind">The preferred endpoint kind (URL or Dapr). Default is URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved endpoint containing base URL and optional Dapr app ID.</returns>
    Task<DiscoveryEndpoint> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default);
}

