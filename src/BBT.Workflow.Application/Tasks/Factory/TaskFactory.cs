using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Factory;

/// <summary>
/// Factory implementation for creating workflow task instances with caching support and performance optimization.
/// This factory ensures task instances are properly isolated from cached data to prevent state pollution.
/// </summary>
public sealed class TaskFactory(
    IComponentCacheStore componentCacheStore,
    ILogger<TaskFactory> logger)
    : ITaskFactory
{
    /// <summary>
    /// Creates a task instance suitable for execution, ensuring it's isolated from cached instances.
    /// </summary>
    /// <param name="taskReference">Reference to the task to be created.</param>
    /// <param name="cancellationToken">Cancellation token for async operations.</param>
    /// <returns>A Result containing the task instance ready for execution, or an error if creation failed.</returns>
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
    /// Uses the task's own Clone method for type-specific optimization.
    /// </summary>
    /// <param name="cachedTask">The cached task instance to create from.</param>
    /// <returns>A Result containing the new task instance isolated from the cached one, or an error if cloning failed.</returns>
    public Result<WorkflowTask> CreateFromCached(WorkflowTask cachedTask)
    {
        if (cachedTask == null)
            return Result<WorkflowTask>.Fail(
                Error.Validation("task.null", "Cached task cannot be null"));

        // Use the task's own optimized Clone method
        var clonedTask = cachedTask.Clone();
        return Result<WorkflowTask>.Ok(clonedTask);
    }
}
