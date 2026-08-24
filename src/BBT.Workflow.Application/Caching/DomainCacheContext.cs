using BBT.Aether.DistributedCache;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides typed cache sets for each workflow component type.
/// All operations delegate directly to Redis via <see cref="CacheSet{T}"/>.
/// </summary>
public class DomainCacheContext : IDomainCacheContext, IDisposable
{
    private readonly Dictionary<string, ICacheSet> _setsByComponentTypeKey;

    public ICacheSet<Definitions.Workflow> Workflows { get; }
    public ICacheSet<WorkflowTask> Tasks { get; }
    public ICacheSet<SchemaDefinition> Schemas { get; }
    public ICacheSet<Function> Functions { get; }
    public ICacheSet<View> Views { get; }
    public ICacheSet<Extension> Extensions { get; }
    public ICacheSet<Mapping> Mappings { get; }

    public DomainCacheContext(
        IDistributedCacheService distributedCache,
        ICacheBackend<Definitions.Workflow> workflowBackend,
        ICacheBackend<WorkflowTask> taskBackend,
        ICacheBackend<SchemaDefinition> schemaBackend,
        ICacheBackend<Function> functionBackend,
        ICacheBackend<View> viewBackend,
        ICacheBackend<Extension> extensionBackend,
        ICacheBackend<Mapping> mappingBackend,
        IComponentGenerationProvider generationProvider,
        IOptions<ComponentCacheOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        IComponentL1Cache l1Cache)
    {
        Workflows = new CacheSet<Definitions.Workflow>(
            distributedCache,
            workflowBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<Definitions.Workflow>>(),
            l1Cache);

        Tasks = new CacheSet<WorkflowTask>(
            distributedCache,
            taskBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<WorkflowTask>>(),
            l1Cache);

        Schemas = new CacheSet<SchemaDefinition>(
            distributedCache,
            schemaBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<SchemaDefinition>>(),
            l1Cache);

        Functions = new CacheSet<Function>(
            distributedCache,
            functionBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<Function>>(),
            l1Cache);

        Views = new CacheSet<View>(
            distributedCache,
            viewBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<View>>(),
            l1Cache);

        Extensions = new CacheSet<Extension>(
            distributedCache,
            extensionBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<Extension>>(),
            l1Cache);

        Mappings = new CacheSet<Mapping>(
            distributedCache,
            mappingBackend,
            generationProvider,
            options,
            timeProvider,
            loggerFactory.CreateLogger<CacheSet<Mapping>>(),
            l1Cache);

        _setsByComponentTypeKey = new Dictionary<string, ICacheSet>(StringComparer.OrdinalIgnoreCase)
        {
            [Definitions.Workflow.ComponentTypeKey] = Workflows,
            [WorkflowTask.ComponentTypeKey] = Tasks,
            [SchemaDefinition.ComponentTypeKey] = Schemas,
            [Function.ComponentTypeKey] = Functions,
            [View.ComponentTypeKey] = Views,
            [Extension.ComponentTypeKey] = Extensions,
            [Mapping.ComponentTypeKey] = Mappings
        };
    }

    public ICacheSet<T> Set<T>() where T : class, IDomainEntity, IReferenceSetter
    {
        if (typeof(T) == typeof(Definitions.Workflow)) return (ICacheSet<T>)Workflows;
        if (typeof(T) == typeof(WorkflowTask)) return (ICacheSet<T>)Tasks;
        if (typeof(T) == typeof(SchemaDefinition)) return (ICacheSet<T>)Schemas;
        if (typeof(T) == typeof(Function)) return (ICacheSet<T>)Functions;
        if (typeof(T) == typeof(View)) return (ICacheSet<T>)Views;
        if (typeof(T) == typeof(Extension)) return (ICacheSet<T>)Extensions;
        if (typeof(T) == typeof(Mapping)) return (ICacheSet<T>)Mappings;

        throw new NotSupportedException($"Type {typeof(T).Name} is not supported in DomainCacheContext.");
    }

    /// <inheritdoc />
    public ICacheSet? Set(string componentTypeKey)
    {
        if (string.IsNullOrWhiteSpace(componentTypeKey))
            return null;

        return _setsByComponentTypeKey.GetValueOrDefault(componentTypeKey);
    }

    public void Dispose()
    {
        Workflows.Dispose();
        Tasks.Dispose();
        Schemas.Dispose();
        Functions.Dispose();
        Views.Dispose();
        Extensions.Dispose();
        Mappings.Dispose();
    }
}
