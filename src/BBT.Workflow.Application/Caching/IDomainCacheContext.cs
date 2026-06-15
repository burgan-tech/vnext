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

    /// <summary>
    /// Gets the cache set for the specified entity type.
    /// </summary>
    /// <typeparam name="T">The entity type</typeparam>
    /// <returns>The cache set for the entity type</returns>
    ICacheSet<T> Set<T>() where T : class, IDomainEntity, IReferenceSetter;
}
