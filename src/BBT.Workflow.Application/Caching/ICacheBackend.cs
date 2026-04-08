using BBT.Aether.Results;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides an abstraction for loading entities from a backend data store.
/// This interface isolates the cache layer from direct database access concerns.
/// </summary>
/// <typeparam name="T">The type of entity to load from the backend</typeparam>
public interface ICacheBackend<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    /// <summary>
    /// Loads an entity from the backend data store based on domain, flow, key, and version.
    /// </summary>
    /// <param name="domain">The domain identifier</param>
    /// <param name="key">The entity key</param>
    /// <param name="version">The entity version. If null, loads the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>
    /// A <see cref="Result{T}"/> containing the entity if found, or <see cref="Error.NotFound"/> if not found.
    /// Infrastructure errors (database, connection) are allowed to throw exceptions and will be handled by middleware.
    /// </returns>
    Task<Result<T>> LoadAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads all active versions of an entity for a given domain and key.
    /// More efficient than calling <see cref="LoadAsync"/> repeatedly when all versions are needed.
    /// </summary>
    Task<Result<List<T>>> LoadAllByKeyAsync(
        string domain,
        string key,
        CancellationToken cancellationToken = default);
}

