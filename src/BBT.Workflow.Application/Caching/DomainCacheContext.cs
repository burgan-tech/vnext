using BBT.Aether.DistributedCache;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides typed cache sets for each workflow component type.
/// All operations delegate directly to Redis via <see cref="CacheSet{T}"/>.
/// </summary>
public class DomainCacheContext : IDomainCacheContext, IDisposable
{
    public ICacheSet<Definitions.Workflow> Workflows { get; }
    public ICacheSet<WorkflowTask> Tasks { get; }
    public ICacheSet<SchemaDefinition> Schemas { get; }
    public ICacheSet<Function> Functions { get; }
    public ICacheSet<View> Views { get; }
    public ICacheSet<Extension> Extensions { get; }

    public DomainCacheContext(
        IDistributedCacheService distributedCache,
        ICacheBackend<Definitions.Workflow> workflowBackend,
        ICacheBackend<WorkflowTask> taskBackend,
        ICacheBackend<SchemaDefinition> schemaBackend,
        ICacheBackend<Function> functionBackend,
        ICacheBackend<View> viewBackend,
        ICacheBackend<Extension> extensionBackend,
        ILoggerFactory loggerFactory)
    {
        Workflows = new CacheSet<Definitions.Workflow>(
            distributedCache,
            workflowBackend,
            loggerFactory.CreateLogger<CacheSet<Definitions.Workflow>>());

        Tasks = new CacheSet<WorkflowTask>(
            distributedCache,
            taskBackend,
            loggerFactory.CreateLogger<CacheSet<WorkflowTask>>());

        Schemas = new CacheSet<SchemaDefinition>(
            distributedCache,
            schemaBackend,
            loggerFactory.CreateLogger<CacheSet<SchemaDefinition>>());

        Functions = new CacheSet<Function>(
            distributedCache,
            functionBackend,
            loggerFactory.CreateLogger<CacheSet<Function>>());

        Views = new CacheSet<View>(
            distributedCache,
            viewBackend,
            loggerFactory.CreateLogger<CacheSet<View>>());

        Extensions = new CacheSet<Extension>(
            distributedCache,
            extensionBackend,
            loggerFactory.CreateLogger<CacheSet<Extension>>());
    }

    public ICacheSet<T> Set<T>() where T : class, IDomainEntity, IReferenceSetter
    {
        if (typeof(T) == typeof(Definitions.Workflow)) return (ICacheSet<T>)Workflows;
        if (typeof(T) == typeof(WorkflowTask)) return (ICacheSet<T>)Tasks;
        if (typeof(T) == typeof(SchemaDefinition)) return (ICacheSet<T>)Schemas;
        if (typeof(T) == typeof(Function)) return (ICacheSet<T>)Functions;
        if (typeof(T) == typeof(View)) return (ICacheSet<T>)Views;
        if (typeof(T) == typeof(Extension)) return (ICacheSet<T>)Extensions;

        throw new NotSupportedException($"Type {typeof(T).Name} is not supported in DomainCacheContext.");
    }

    public void Dispose()
    {
        Workflows.Dispose();
        Tasks.Dispose();
        Schemas.Dispose();
        Functions.Dispose();
        Views.Dispose();
        Extensions.Dispose();
    }
}
