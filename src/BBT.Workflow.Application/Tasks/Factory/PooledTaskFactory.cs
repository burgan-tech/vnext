using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using BBT.Workflow.Definitions.Tasks;

namespace BBT.Workflow.Tasks.Factory;

/// <summary>
/// Advanced task factory implementation with object pooling for high-performance scenarios.
/// This factory uses memory pooling to reduce garbage collection pressure and improve performance.
/// </summary>
public sealed class PooledTaskFactory(
    IComponentCacheStore componentCacheStore,
    ILogger<PooledTaskFactory> logger,
    IOptions<TaskFactoryOptions> options,
    IWorkflowMetrics workflowMetrics)
    : ITaskFactory
{
    private readonly ConcurrentDictionary<Type, ObjectPool<WorkflowTask>> _pools = new();
    private readonly TaskFactoryOptions _options = options.Value;

    /// <summary>
    /// Creates a task instance suitable for execution, ensuring it's isolated from cached instances.
    /// Uses object pooling for better performance in high-throughput scenarios.
    /// </summary>
    public async Task<WorkflowTask> CreateExecutionTaskAsync(
        IReference taskReference, 
        CancellationToken cancellationToken = default)
    {
        try
        {
            var cachedTask = await componentCacheStore.GetTaskAsync(taskReference, cancellationToken);
            return CreateFromCached(cachedTask);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create execution task for reference: {TaskReference}", taskReference);
            throw;
        }
    }

    /// <summary>
    /// Creates a task instance from cached data with optimized cloning strategy.
    /// Since task properties have private setters, we use the efficient Clone() method
    /// instead of true object pooling, but maintain the pool for future optimization.
    /// </summary>
    public WorkflowTask CreateFromCached(WorkflowTask cachedTask)
    {
        if (cachedTask == null)
            throw new ArgumentNullException(nameof(cachedTask));

        try
        {
            var taskType = cachedTask.GetType();
            
            // Use configuration to determine strategy
            if (ShouldUsePoolingFromConfig(taskType))
            {
                // Use real object pooling with the new CopyFromInternal methods
                return CreateFromPool(cachedTask);
            }
            
            // Fall back to direct cloning for non-pooled types
            var directClonedTask = cachedTask.Clone();
            
            logger.LogDebug("Successfully cloned task {TaskKey} of type {TaskType}", 
                cachedTask.Key, taskType.Name);
            
            return directClonedTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clone task {TaskKey} of type {TaskType}", 
                cachedTask.Key, cachedTask.GetType().Name);
            throw;
        }
    }

    private WorkflowTask CreateFromPool(WorkflowTask template)
    {
        var taskType = template.GetType();
        var taskTypeName = taskType.Name;
        var pool = _pools.GetOrAdd(taskType, _ => CreatePoolForType(taskType));
        
        // Record pool rental metric
        workflowMetrics.RecordTaskFactoryPoolRental(taskTypeName);
        
        var pooledTask = pool.Get();
        
        // Update pool state metrics (approximate tracking)
        // Note: These are estimates since DefaultObjectPool doesn't expose exact counts
        UpdatePoolStateMetrics(taskTypeName, isRenting: true);
        
        // Copy properties from template to pooled instance using efficient internal methods
        CopyTaskProperties(template, pooledTask);
        
        logger.LogDebug("Retrieved and configured task from pool for type {TaskType}", taskType.Name);
        
        return pooledTask;
    }

    /// <summary>
    /// Updates pool state metrics to reflect current pool usage.
    /// This provides approximate tracking since DefaultObjectPool doesn't expose exact counts.
    /// </summary>
    private void UpdatePoolStateMetrics(string taskTypeName, bool isRenting)
    {
        // Since DefaultObjectPool doesn't expose internal counts, we use estimated values
        // This is a best-effort tracking mechanism for monitoring purposes
        
        if (isRenting)
        {
            // When renting, we assume available decreases and in-use increases
            // These are estimates for monitoring trends
            logger.LogTrace("Object rented from pool for task type {TaskType}", taskTypeName);
        }
        
        // For more accurate metrics, consider implementing a custom object pool
        // that tracks exact counts or using a third-party pool with metrics support
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
        var policy = new TaskPooledObjectPolicy(taskType, workflowMetrics);
        var pool = new DefaultObjectPool<WorkflowTask>(policy, _options.MaxPoolSize);
        
        // Initialize pool size metric
        workflowMetrics.SetTaskFactoryPoolSize(taskType.Name, _options.MaxPoolSize);
        workflowMetrics.SetTaskFactoryPoolAvailable(taskType.Name, _options.MaxPoolSize);
        workflowMetrics.SetTaskFactoryPoolInUse(taskType.Name, 0);
        
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
internal sealed class TaskPooledObjectPolicy(Type taskType, IWorkflowMetrics workflowMetrics) : IPooledObjectPolicy<WorkflowTask>
{
    private readonly string _taskTypeName = taskType.Name;

    public WorkflowTask Create()
    {
        // Record object creation metric
        workflowMetrics.RecordTaskFactoryPoolCreate(_taskTypeName);
        
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
        workflowMetrics.RecordTaskFactoryPoolReturn(_taskTypeName);
        
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
            
        RegisterPoolableTask<DaprHttpEndpointTask>(
            DaprHttpEndpointTask.CreateEmpty,
            (source, target) => ((DaprHttpEndpointTask)target).CopyFromInternal((DaprHttpEndpointTask)source));
            
        RegisterPoolableTask<HumanTask>(
            HumanTask.CreateEmpty,
            (source, target) => ((HumanTask)target).CopyFromInternal((HumanTask)source));
            
        RegisterPoolableTask<SignalRTask>(
            SignalRTask.CreateEmpty,
            (source, target) => ((SignalRTask)target).CopyFromInternal((SignalRTask)source));
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