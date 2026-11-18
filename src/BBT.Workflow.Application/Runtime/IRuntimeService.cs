using BBT.Workflow.Instances;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Service for accessing runtime workflow entities from the database.
/// Automatically infers the schema from the entity type being requested.
/// </summary>
/// <remarks>
/// Nothing here loads the bodies of a component's whole publish history: versions are listed and resolved
/// from the version column alone, and only the one chosen body is deserialized. A long history of large
/// definitions loaded that way exhausted memory and, while failures were skipped row by row, resolved
/// floating requests to an older version.
/// </remarks>
public interface IRuntimeService
{
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
    /// Returns the stored version strings of every active version of an entity by key, without loading or
    /// deserializing any body. Use this to decide which version a request means, then load only that one
    /// with <see cref="GetAsync{T}(string, string, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="T">The entity type; selects the schema the versions are read from</typeparam>
    /// <param name="key">The entity key to filter by</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>The distinct stored version strings; empty when the key has no active version</returns>
    Task<IReadOnlyList<string>> GetVersionsAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Pages the published versions of an entity by key, newest publish first, with their publish time and
    /// no body. The rows are the ones <see cref="GetVersionsAsync{T}"/> lists.
    /// </summary>
    /// <typeparam name="T">The entity type; selects the schema the versions are read from</typeparam>
    /// <param name="key">The entity key to filter by</param>
    /// <param name="skip">Rows to skip</param>
    /// <param name="take">Rows to return; pass the page size plus one to learn whether a next page exists</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    Task<IReadOnlyList<ComponentVersionSummary>> GetVersionPageAsync<T>(
        string key, int skip, int take, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Returns the key, version and publish time of every published version of every entity of the type,
    /// without loading any body.
    /// </summary>
    /// <typeparam name="T">The entity type; selects the schema the versions are read from</typeparam>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    Task<IReadOnlyList<PublishedComponentVersion>> GetPublishedVersionsAsync<T>(
        CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Returns the distributed cache keys for all active entities of the specified type without
    /// loading the entity data. Used by broadcast-receiving pods to warm their in-memory cache
    /// from the distributed cache without a full DB scan.
    /// </summary>
    Task<IEnumerable<string>> GetActiveCacheKeysAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;
}
