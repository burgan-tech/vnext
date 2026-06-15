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
    ///     <item><description>null/empty/"latest": Returns the latest version</description></item>
    ///     <item><description>Full version (e.g., "1.0.0-pkg.1.17.0+account"): Exact match from Redis/DB</description></item>
    ///     <item><description>Artifact version (e.g., "1.0.0"): Returns highest pkg version for that artifact</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the entity if found, or <see cref="Error.NotFound"/> if not found.
    /// </returns>
    Task<Result<T>> GetByVersionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an entity in the cache. Writes to Redis under full, latest, and artifact keys.
    /// Used by CastHandlers on publish to immediately populate cache for all pods.
    /// </summary>
    /// <returns>
    /// A <see cref="Result"/> indicating success or failure of the cache operation.
    /// </returns>
    Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates (removes) cache entries for a component version.
    /// Removes the latest, artifact, and full version keys from Redis.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key</param>
    /// <param name="version">The version to invalidate</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>
    /// A <see cref="Result"/> indicating success or failure of the invalidation operation.
    /// </returns>
    Task<Result> InvalidateAsync(string domain, string key, string version, CancellationToken cancellationToken = default);
}
