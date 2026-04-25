using BBT.Aether.Results;

namespace BBT.Workflow.Caching;

/// <summary>
/// Non-generic marker interface for cache sets
/// </summary>
public interface ICacheSet : IDisposable
{
    /// <summary>
    /// Gets the type of entity managed by this cache set.
    /// </summary>
    Type EntityType { get; }

    Task LoadAllAsync(object data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all entities into both in-memory and distributed cache.
    /// Used when initiating cache refresh that should propagate to all pods.
    /// </summary>
    Task LoadAllWithDistributedCacheAsync(object data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the provided entities into the existing in-memory snapshot without replacing entries
    /// that are not present in <paramref name="data"/>. Used for incremental (delta) cache updates.
    /// </summary>
    Task MergeAllAsync(object data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Warms the local in-memory cache from the distributed cache for the given cache keys.
    /// For each key: reads from distributed cache; if present upserts locally (no write-back).
    /// On a distributed cache miss falls back to a per-key DB query and upserts locally.
    /// Used by broadcast-receiving pods to avoid a full DB scan.
    /// </summary>
    Task LoadFromDistributedCacheAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic interface for strongly-typed cache set operations
/// </summary>
/// <typeparam name="T">The type of entity to cache</typeparam>
public interface ICacheSet<T> : ICacheSet where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Retrieves an entity from the cache by its cache key.
    /// </summary>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the entity if found, or <see cref="Error.NotFound"/> if not found.
    /// </returns>
    Task<Result<T>> GetAsync(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an entity in the cache.
    /// </summary>
    /// <returns>
    /// A <see cref="Result"/> indicating success or failure of the cache operation.
    /// </returns>
    Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all versions of an entity by domain and name.
    /// </summary>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a list of entities. Returns empty list if none found (success with empty collection).
    /// </returns>
    Task<Result<List<T>>> GetAllByNameAsync(string domain, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all latest entities for a domain.
    /// </summary>
    /// <returns>
    /// A <see cref="Result{T}"/> containing a list of entities. Returns empty list if none found (success with empty collection).
    /// </returns>
    Task<Result<List<T>>> GetAllByDomainAsync(string domain, CancellationToken cancellationToken = default);

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
    ///     <item><description>null/empty: Returns the latest version</description></item>
    ///     <item><description>Full version (e.g., "1.0.0-pkg.1.17.0+account"): Exact match</description></item>
    ///     <item><description>Artifact version (e.g., "1.0.0" or "1.0.0-alpha.1"): Returns highest pkg version for that artifact</description></item>
    ///     <item><description>Partial version (e.g., "1.0"): Returns highest version matching the prefix</description></item>
    /// </list>
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the entity if found, or <see cref="Error.NotFound"/> if not found.
    /// </returns>
    Task<Result<T>> GetByVersionAsync(string domain, string key, string? version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates (removes) an entity from the cache.
    /// </summary>
    /// <returns>
    /// A <see cref="Result"/> indicating success or failure of the invalidation operation.
    /// </returns>
    Task<Result> InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs cleanup of expired and least-used items from the cache.
    /// </summary>
    /// <param name="ttl">Time-to-live for cache items. Items older than this are removed.</param>
    /// <param name="maxItems">Maximum number of items to keep. If cache exceeds this, least-used items are removed.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>The number of items removed from the cache</returns>
    int Cleanup(TimeSpan? ttl = null, int? maxItems = null, CancellationToken cancellationToken = default);
}