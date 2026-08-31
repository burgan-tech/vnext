using System.Collections.Concurrent;
using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Tasks.Factory;

/// <summary>
/// Advanced task factory implementation with object pooling for high-performance scenarios.
/// This factory uses memory pooling to reduce garbage collection pressure and improve performance.
/// </summary>
public sealed class PooledTaskFactory(
    IComponentCacheStore componentCacheStore,
    ILogger<PooledTaskFactory> logger,
    IOptions<TaskFactoryOptions> options)
    : ITaskFactory
{
    private readonly ConcurrentDictionary<Type, ObjectPool<WorkflowTask>> _pools = new();
    private readonly TaskFactoryOptions _options = options.Value;

    /// <summary>
    /// Creates a task instance suitable for execution, ensuring it's isolated from cached instances.
    /// Uses object pooling for better performance in high-throughput scenarios.
    /// </summary>
    public async Task<Result<WorkflowTask>> CreateExecutionTaskAsync(
        IReference taskReference,
        CancellationToken cancellationToken = default)
    {
        // Task.Resolve lives INSIDE the factory (not at the engine call site) so FanOut and
        // CacheAside resolutions are covered too. Always-on Business span — this was the
        // unattributed head of Task.Execute.
        using var resolveActivity = TaskExecutionActivityHelper.StartActivity(
            TaskExecutionActivityHelper.OperationResolve, taskReference.Key);

        return await componentCacheStore.GetTaskAsync(taskReference, cancellationToken)
            .Then(CreateFromCached)
            .OnFailure(error =>
            {
                TaskExecutionActivityHelper.SetError(Activity.Current, error.Message, "TaskFactoryError");
                logger.LogError(
                    "Failed to create execution task for reference {TaskReference}: {ErrorCode}",
                    taskReference.ToString(), error.Code);
            });
    }

    /// <summary>
    /// Creates a task instance from cached data with optimized cloning strategy.
    /// Since task properties have private setters, we use the efficient Clone() method
    /// instead of true object pooling, but maintain the pool for future optimization.
    /// </summary>
    public Result<WorkflowTask> CreateFromCached(WorkflowTask cachedTask)
    {
        if (cachedTask == null)
            return Result<WorkflowTask>.Fail(
                Error.Validation("task.null", "Cached task cannot be null"));

        var taskType = cachedTask.GetType();

        // Use configuration to determine strategy
        if (ShouldUsePoolingFromConfig(taskType))
        {
            // Use real object pooling with the new CopyFromInternal methods
            return Result<WorkflowTask>.Ok(CreateFromPool(cachedTask));
        }

        // Fall back to direct cloning for non-pooled types
        var directClonedTask = cachedTask.Clone();
        return Result<WorkflowTask>.Ok(directClonedTask);
    }

    private WorkflowTask CreateFromPool(WorkflowTask template)
    {
        var taskType = template.GetType();
        var taskTypeName = taskType.Name;
        var pool = _pools.GetOrAdd(taskType, _ => CreatePoolForType(taskType));

        // Record pool rental metric

        var pooledTask = pool.Get();
        // Copy properties from template to pooled instance using efficient internal methods
        CopyTaskProperties(template, pooledTask);
        return pooledTask;
    }

    /// <summary>
    /// Copies all task properties using efficient internal methods for object pooling
    /// </summary>
    private static void CopyTaskProperties(WorkflowTask source, WorkflowTask target)
    {
        // Try registry-based approach first
        if (PoolableTaskRegistry.TryCopyProperties(source, target))
        {
            return;
        }

        // Fallback to base copy for unsupported types
        source.CopyBaseToInternal(target);
    }

    private ObjectPool<WorkflowTask> CreatePoolForType(Type taskType)
    {
        // Pool creation logic kept for future use
        var policy = new TaskPooledObjectPolicy(taskType);
        var pool = new DefaultObjectPool<WorkflowTask>(policy, _options.MaxPoolSize);

        // Initialize pool size metric

        return pool;
    }

    private bool ShouldUsePoolingFromConfig(Type taskType)
    {
        return _options.PooledTaskTypes.Contains(taskType.Name);
    }
}

/// <summary>
/// Object pool policy for creating workflow task instances.
/// </summary>
internal sealed class TaskPooledObjectPolicy(Type taskType)
    : IPooledObjectPolicy<WorkflowTask>
{
    private readonly string _taskTypeName = taskType.Name;

    public WorkflowTask Create()
    {
        // Record object creation metric

        // Try registry-based creation first
        var task = PoolableTaskRegistry.TryCreateEmpty(taskType);
        if (task != null)
        {
            return task;
        }

        // Fallback to reflection for non-registered types
        return (WorkflowTask)Activator.CreateInstance(taskType, true)!;
    }

    public bool Return(WorkflowTask obj)
    {
        // Record object return metric

        // Reset the object state before returning to pool
        obj.Reset();
        return true;
    }
}

/// <summary>
/// Registry for poolable task type operations
/// </summary>
public static class PoolableTaskRegistry
{
    private static readonly ConcurrentDictionary<Type, Func<WorkflowTask>> Creators = new();
    private static readonly ConcurrentDictionary<Type, Action<WorkflowTask, WorkflowTask>> Copiers = new();

    static PoolableTaskRegistry()
    {
        // Register all poolable task types
        RegisterPoolableTask<DaprServiceTask>(
            DaprServiceTask.CreateEmpty,
            (source, target) => ((DaprServiceTask)target).CopyFromInternal((DaprServiceTask)source));

        RegisterPoolableTask<HttpTask>(
            HttpTask.CreateEmpty,
            (source, target) => ((HttpTask)target).CopyFromInternal((HttpTask)source));

        RegisterPoolableTask<ScriptTask>(
            ScriptTask.CreateEmpty,
            (source, target) => ((ScriptTask)target).CopyFromInternal((ScriptTask)source));

        RegisterPoolableTask<ConditionTask>(
            ConditionTask.CreateEmpty,
            (source, target) => ((ConditionTask)target).CopyFromInternal((ConditionTask)source));

        RegisterPoolableTask<DaprBindingTask>(
            DaprBindingTask.CreateEmpty,
            (source, target) => ((DaprBindingTask)target).CopyFromInternal((DaprBindingTask)source));

        RegisterPoolableTask<DaprPubSubTask>(
            DaprPubSubTask.CreateEmpty,
            (source, target) => ((DaprPubSubTask)target).CopyFromInternal((DaprPubSubTask)source));

        RegisterPoolableTask<StateStoreTask>(
            StateStoreTask.CreateEmpty,
            (source, target) => ((StateStoreTask)target).CopyFromInternal((StateStoreTask)source));

        RegisterPoolableTask<CacheAsideTask>(
            CacheAsideTask.CreateEmpty,
            (source, target) => ((CacheAsideTask)target).CopyFromInternal((CacheAsideTask)source));

        RegisterPoolableTask<DaprHttpEndpointTask>(
            DaprHttpEndpointTask.CreateEmpty,
            (source, target) => ((DaprHttpEndpointTask)target).CopyFromInternal((DaprHttpEndpointTask)source));

        RegisterPoolableTask<HumanTask>(
            HumanTask.CreateEmpty,
            (source, target) => ((HumanTask)target).CopyFromInternal((HumanTask)source));
        RegisterPoolableTask<NotificationTask>(
            NotificationTask.CreateEmpty,
            (source, target) => ((NotificationTask)target).CopyFromInternal((NotificationTask)source));
        RegisterPoolableTask<StartTask>(
            StartTask.CreateEmpty,
            (source, target) => ((StartTask)target).CopyFromInternal((StartTask)source));
        RegisterPoolableTask<DirectTriggerTask>(
            DirectTriggerTask.CreateEmpty,
            (source, target) => ((DirectTriggerTask)target).CopyFromInternal((DirectTriggerTask)source));
        RegisterPoolableTask<GetInstanceDataTask>(
            GetInstanceDataTask.CreateEmpty,
            (source, target) => ((GetInstanceDataTask)target).CopyFromInternal((GetInstanceDataTask)source));
        RegisterPoolableTask<SubProcessTask>(
            SubProcessTask.CreateEmpty,
            (source, target) => ((SubProcessTask)target).CopyFromInternal((SubProcessTask)source));
        RegisterPoolableTask<GetInstanceTask>(
            GetInstanceTask.CreateEmpty,
            (source, target) => ((GetInstanceTask)target).CopyFromInternal((GetInstanceTask)source));
    }

    public static void RegisterPoolableTask<T>(
        Func<WorkflowTask> creator,
        Action<WorkflowTask, WorkflowTask> copier) where T : WorkflowTask
    {
        var type = typeof(T);
        Creators[type] = creator;
        Copiers[type] = copier;
    }

    public static WorkflowTask? TryCreateEmpty(Type taskType)
    {
        return Creators.TryGetValue(taskType, out var creator) ? creator() : null;
    }

    public static bool TryCopyProperties(WorkflowTask source, WorkflowTask target)
    {
        var sourceType = source.GetType();
        if (Copiers.TryGetValue(sourceType, out var copier))
        {
            copier(source, target);
            return true;
        }

        return false;
    }
}
