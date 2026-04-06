using BBT.Workflow.Definitions;

namespace BBT.Workflow.Caching;

/// <summary>
/// Interface for domain cache context
/// </summary>
public interface IDomainCacheContext
{
    ICacheSet<Definitions.Workflow> Workflows { get; }
    ICacheSet<WorkflowTask> Tasks { get; }
    ICacheSet<SchemaDefinition> Schemas { get; }
    ICacheSet<Function> Functions { get; }
    ICacheSet<View> Views { get; }
    ICacheSet<Extension> Extensions { get; }

    Task InitializeAsync(Dictionary<Type, object> initialData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes both in-memory and distributed cache from the provided data.
    /// Use this when triggering cache refresh that should propagate to all pods.
    /// </summary>
    /// <param name="initialData">Dictionary mapping entity types to their data</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A task representing the asynchronous initialization operation</returns>
    Task InitializeWithDistributedCacheAsync(Dictionary<Type, object> initialData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts only the provided (delta) entities into the existing in-memory cache without
    /// replacing entries absent from <paramref name="deltaData"/>. Used for incremental updates.
    /// </summary>
    /// <param name="deltaData">Dictionary mapping entity types to the changed/new entities</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    Task MergeAsync(Dictionary<Type, object> deltaData,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the cache set for the specified entity type.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <returns>The cache set for the entity type</returns>
    ICacheSet<T> Set<T>() where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Performs cleanup on all cache sets, removing expired and least-used items.
    /// </summary>
    /// <param name="ttl">Time-to-live for cache items</param>
    /// <param name="maxItemsPerSet">Maximum items per cache set</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>Total number of items removed across all cache sets</returns>
    int CleanupAll(
        TimeSpan? ttl = null,
        int? maxItemsPerSet = null,
        CancellationToken cancellationToken = default);
}

