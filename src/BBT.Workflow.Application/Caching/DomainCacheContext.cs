using BBT.Aether.DistributedCache;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

public class DomainCacheContext : CacheContext, IDomainCacheContext, IDisposable
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

        CacheSets =
        [
            Workflows,
            Tasks,
            Schemas,
            Functions,
            Views,
            Extensions
        ];
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

    public new Task InitializeAsync(Dictionary<Type, object> initialData, CancellationToken cancellationToken = default)
        => base.InitializeAsync(initialData, cancellationToken);

    public async Task InitializeWithDistributedCacheAsync(Dictionary<Type, object> initialData, CancellationToken cancellationToken = default)
    {
        foreach (var cacheSet in CacheSets)
        {
            var cacheSetType = cacheSet.GetType().GetGenericArguments()[0];

            if (initialData.TryGetValue(cacheSetType, out var data))
            {
                await cacheSet.LoadAllWithDistributedCacheAsync(data, cancellationToken);
            }
        }
    }

    public int CleanupAll(
        TimeSpan? ttl = null,
        int? maxItemsPerSet = null,
        CancellationToken cancellationToken = default)
    {
        var total = 0;

        foreach (var cacheSet in CacheSets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (cacheSet is ICacheSet<Definitions.Workflow> wf && cacheSet.EntityType == typeof(Definitions.Workflow))
                total += wf.Cleanup(ttl, maxItemsPerSet, cancellationToken);
            else if (cacheSet is ICacheSet<WorkflowTask> wt && cacheSet.EntityType == typeof(WorkflowTask))
                total += wt.Cleanup(ttl, maxItemsPerSet, cancellationToken);
            else if (cacheSet is ICacheSet<SchemaDefinition> sd && cacheSet.EntityType == typeof(SchemaDefinition))
                total += sd.Cleanup(ttl, maxItemsPerSet, cancellationToken);
            else if (cacheSet is ICacheSet<Function> fn && cacheSet.EntityType == typeof(Function))
                total += fn.Cleanup(ttl, maxItemsPerSet, cancellationToken);
            else if (cacheSet is ICacheSet<View> vw && cacheSet.EntityType == typeof(View))
                total += vw.Cleanup(ttl, maxItemsPerSet, cancellationToken);
            else if (cacheSet is ICacheSet<Extension> ex && cacheSet.EntityType == typeof(Extension))
                total += ex.Cleanup(ttl, maxItemsPerSet, cancellationToken);
        }

        return total;
    }

    public void Dispose()
    {
        foreach (var cacheSet in CacheSets)
            cacheSet.Dispose();
    }
}
