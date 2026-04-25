namespace BBT.Workflow.Runtime;

/// <summary>
/// Service for accessing runtime workflow entities from the database.
/// Automatically infers the schema from the entity type being requested.
/// </summary>
public interface IRuntimeService
{
    /// <summary>
    /// Retrieves all active entities of the specified type.
    /// The schema is automatically inferred from the entity type.
    /// </summary>
    /// <typeparam name="T">The type of entity to retrieve</typeparam>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A collection of entities of the specified type</returns>
    Task<IEnumerable<T?>> GetAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Retrieves active entities of the specified type modified at or after <paramref name="since"/>.
    /// When <paramref name="since"/> is <c>null</c>, all active entities are returned (full load).
    /// The schema is automatically inferred from the entity type.
    /// </summary>
    Task<IEnumerable<T?>> GetAsync<T>(DateTime? since, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Retrieves a specific entity by key and version.
    /// The schema is automatically inferred from the entity type.
    /// </summary>
    /// <typeparam name="T">The type of entity to retrieve</typeparam>
    /// <param name="key">The entity key</param>
    /// <param name="version">The entity version</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>The entity if found, otherwise null</returns>
    Task<T?> GetAsync<T>(string key, string version, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Retrieves all active versions of an entity by key.
    /// More efficient than <see cref="GetAsync{T}(CancellationToken)"/> when only a specific key is needed.
    /// The schema is automatically inferred from the entity type.
    /// </summary>
    /// <typeparam name="T">The type of entity to retrieve</typeparam>
    /// <param name="key">The entity key to filter by</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>All active entities matching the given key</returns>
    Task<IEnumerable<T?>> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Returns the distributed cache keys for all active entities of the specified type without
    /// loading the entity data. Used by broadcast-receiving pods to warm their in-memory cache
    /// from the distributed cache without a full DB scan.
    /// </summary>
    Task<IEnumerable<string>> GetActiveCacheKeysAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;
}