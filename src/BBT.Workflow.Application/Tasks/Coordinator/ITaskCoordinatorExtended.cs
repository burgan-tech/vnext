using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Extended task coordinator interface that provides detailed execution results
/// for Error Boundary integration.
/// </summary>
/// <remarks>
/// This interface extends the base ITaskCoordinator with methods that return
/// detailed execution information including task errors for Error Boundary
/// policy resolution.
/// </remarks>
public interface ITaskCoordinatorExtended : ITaskCoordinator
{
    /// <summary>
    /// Coordinates a collection of tasks and returns detailed execution results
    /// including task-level error information for Error Boundary handling.
    /// </summary>
    /// <param name="onExecuteTasks">Collection of tasks to be coordinated.</param>
    /// <param name="instanceTransitionId">The instance transition context. Can be null for extension tasks.</param>
    /// <param name="taskTrigger">The trigger type that initiated the task execution.</param>
    /// <param name="context">The script execution context containing instance data and task responses.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    /// <returns>A Result containing detailed execution information including task errors.</returns>
    Task<Result<TasksExecutionResult>> ExecuteWithDetailsAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Coordinates a collection of tasks with bypass support for retry scenarios.
    /// Skips tasks that have already completed successfully during a previous attempt.
    /// </summary>
    /// <param name="onExecuteTasks">Collection of tasks to be coordinated.</param>
    /// <param name="instanceTransitionId">The instance transition context. Can be null for extension tasks.</param>
    /// <param name="taskTrigger">The trigger type that initiated the task execution.</param>
    /// <param name="context">The script execution context containing instance data and task responses.</param>
    /// <param name="completedTaskIds">Collection of task IDs to skip (already completed).</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    /// <returns>A Result containing detailed execution information including task errors.</returns>
    Task<Result<TasksExecutionResult>> ExecuteWithDetailsAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        IEnumerable<string> completedTaskIds,
        CancellationToken cancellationToken = default);
}
