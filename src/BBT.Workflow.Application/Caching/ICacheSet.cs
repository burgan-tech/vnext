using BBT.Aether.Results;

namespace BBT.Workflow.Caching;

/// <summary>
/// Non-generic marker interface for cache sets.
/// </summary>
public interface ICacheSet : IDisposable
{
    /// <summary>
    /// Gets the type of entity managed by this cache set.
    /// </summary>
    Type EntityType { get; }

    /// <summary>
    /// Invalidates cached data for a component, so the next read re-resolves it from the database.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key</param>
    /// <param name="version">
    /// The version that changed. A full version additionally drops that version's immutable body;
    /// any other form only invalidates resolutions.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A <see cref="Result"/> indicating success or failure of the invalidation operation.</returns>
    /// <remarks>
    /// Every cached answer to a version <i>request</i> is invalidated, not just the ones derivable from
    /// <paramref name="version"/> — adding or removing any version can change what <c>latest</c>,
    /// <c>1</c> or <c>1.2</c> resolve to. Call this when a version is deactivated or deleted; publishing
    /// already invalidates as part of <see cref="ICacheSet{T}.SetAsync"/>.
    /// <para>
    /// Declared on the non-generic interface so callers holding only a component type key (for example a
    /// cast handler dispatching on <c>sys-views</c>) can invalidate without knowing the entity type.
    /// </para>
    /// </remarks>
    Task<Result> InvalidateAsync(string domain, string key, string version, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic interface for strongly-typed cache set operations.
/// Provides Redis-first caching with DB fallback for workflow components.
/// </summary>
/// <typeparam name="T">The type of entity to cache</typeparam>
public interface ICacheSet<T> : ICacheSet where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Retrieves the latest version of an entity by domain and name.
    /// </summary>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the entity if found, or <see cref="Error.NotFound"/> if not found.
    /// </returns>
    Task<Result<T>> GetLatestByNameAsync(string domain, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an entity by domain, name, and version with smart version matching.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key/name</param>
    /// <param name="version">The version to search for. Supports multiple formats:
    /// <list type="bullet">
    ///     <item><description>null/empty/"latest": the highest available version</description></item>
    ///     <item><description>Full version (e.g., "1.5.6-pkg.1.1.56+core"): that exact revision. Build metadata is ignored, so "1.5.6-pkg.1.1.56" resolves identically</description></item>
    ///     <item><description>Artifact version (e.g., "2.3.5"): the highest package version of that artifact</description></item>
    ///     <item><description>Partial version (e.g., "1.2"): the highest version whose artifact major and minor match</description></item>
    ///     <item><description>Major-only version (e.g., "1"): the highest version whose artifact major matches</description></item>
    /// </list>
    /// The artifact version dominates the package version when ranking, so "1.6.0-pkg.1.0.0" outranks
    /// "1.5.0-pkg.9.9.9".
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the entity if found, or <see cref="Error.NotFound"/> if not found.
    /// </returns>
    Task<Result<T>> GetByVersionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a published entity in the cache and invalidates every stale version resolution for its
    /// component. Called by CastHandlers on publish, so the effect is immediately visible to all pods.
    /// </summary>
    /// <returns>
    /// A <see cref="Result"/> indicating success or failure of the cache operation.
    /// </returns>
    /// <remarks>
    /// The entity's own body is written under its immutable full-version key. Cached answers to version
    /// <i>requests</i> are then invalidated wholesale and the common ones re-resolved, because the entity
    /// being published is not necessarily the winner of the ranges it belongs to — publishes are not
    /// monotonic, and a lower version may be released deliberately.
    /// </remarks>
    Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default);
}
