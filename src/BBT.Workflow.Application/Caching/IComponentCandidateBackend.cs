using BBT.Aether.Results;

namespace BBT.Workflow.Caching;

/// <summary>
/// An <see cref="ICacheBackend{T}"/> that can also report which stored versions it could not load.
/// </summary>
/// <remarks>
/// Version resolution only sees the candidates it is handed. When a stored row is left out (for
/// example because it no longer deserializes), the resolver silently picks the best remaining version,
/// which can be an older build. <see cref="CacheSet{T}"/> uses this to log the loss and to keep such a
/// degraded answer short-lived instead of caching it for the full resolution TTL. A backend that does
/// not implement it is treated as never dropping anything.
/// </remarks>
/// <typeparam name="T">The type of entity loaded from the backend</typeparam>
public interface IComponentCandidateBackend<T> : ICacheBackend<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Loads every active version of a component key in the given domain, and the versions of the rows
    /// that were found but dropped.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    Task<Result<ComponentCandidates<T>>> LoadCandidatesAsync(
        string domain,
        string key,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The candidate versions a version resolution is computed from.
/// </summary>
/// <typeparam name="T">The component entity type</typeparam>
/// <param name="Entities">The loaded versions.</param>
/// <param name="DroppedVersions">Versions found in the store but left out of <paramref name="Entities"/>.</param>
public sealed record ComponentCandidates<T>(List<T> Entities, IReadOnlyList<string> DroppedVersions)
    where T : class
{
    /// <summary>Whether any stored version was left out of <see cref="Entities"/>.</summary>
    public bool HasDropped => DroppedVersions.Count > 0;
}
