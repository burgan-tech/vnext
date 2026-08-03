using BBT.Workflow.Definitions;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides access to typed cache sets for each workflow component type.
/// All cache operations go directly to Redis (shared across pods).
/// </summary>
public interface IDomainCacheContext
{
    ICacheSet<Definitions.Workflow> Workflows { get; }
    ICacheSet<WorkflowTask> Tasks { get; }
    ICacheSet<SchemaDefinition> Schemas { get; }
    ICacheSet<Function> Functions { get; }
    ICacheSet<View> Views { get; }
    ICacheSet<Extension> Extensions { get; }
    ICacheSet<Mapping> Mappings { get; }

    /// <summary>
    /// Gets the cache set for the specified entity type.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <returns>The cache set for the entity type</returns>
    ICacheSet<T> Set<T>() where T : class, IDomainEntity, IReferenceSetter;

    /// <summary>
    /// Gets the cache set for the specified component type key, for callers that only have the key as a
    /// string (for example a cast handler dispatching on <c>sys-views</c>).
    /// </summary>
    /// <param name="componentTypeKey">The component type key (e.g. <c>sys-views</c>, <c>sys-flows</c>)</param>
    /// <returns>The matching cache set, or null when the key does not name a cached component type.</returns>
    ICacheSet? Set(string componentTypeKey);
}
