using BBT.Aether.Results;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Caching;

/// <summary>
/// Optional companion of <see cref="ICacheBackend{T}"/> for a backend that can report which stored
/// versions it had to leave out of a key-wide load.
/// </summary>
/// <remarks>
/// <see cref="CacheSet{T}"/> resolves floating version requests (<c>latest</c>, an artifact version,
/// <c>MAJOR.MINOR</c>) over the versions a backend returns, then caches the winner for
/// <see cref="ComponentCacheOptions.ResolutionTtlSeconds"/>. When the backend silently drops a stored
/// version, the winner can be an older build, and that stale answer would then be served for the whole
/// TTL (issue #1192). A backend implementing this interface lets the cache see the gap, warn about it
/// and keep the answer short-lived. A backend without it is treated as reporting no gaps.
/// </remarks>
/// <typeparam name="T">The component type.</typeparam>
public interface ICacheCandidateSource<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Loads every active version of a component key in the given domain, together with the stored
    /// versions that could not be loaded.
    /// </summary>
    /// <param name="domain">The domain identifier.</param>
    /// <param name="key">The component key.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The loaded versions and the dropped ones; either list may be empty.</returns>
    Task<Result<ComponentCandidateSet<T>>> LoadCandidatesByKeyAsync(
        string domain,
        string key,
        CancellationToken cancellationToken = default);
}
