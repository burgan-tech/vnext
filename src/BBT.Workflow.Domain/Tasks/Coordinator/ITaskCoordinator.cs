using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Coordinates workflow task execution with support for both parallel and sequential execution strategies.
/// Uses the Result pattern for Railway-oriented error handling.
/// </summary>
/// <remarks>
/// The ITaskCoordinator interface defines the contract for coordinating task execution based on dependencies and provides 
/// both condition evaluation and task coordination capabilities. It manages task state transitions
/// and maintains execution context throughout the workflow process.
/// 
/// This coordinator delegates individual task execution to ITaskExecutor implementations (via ITaskExecutorRegistry)
/// while managing the overall execution flow (parallel vs sequential).
/// 
/// Error boundary handling is applied at the ITaskExecutor level via TaskExecutorMiddleware,
/// allowing per-task retry and error policy resolution.
/// </remarks>
public interface ITaskCoordinator : ITaskConditionService, ITaskTimerService
{
    /// <summary>
    /// Coordinates a collection of tasks using the optimal execution strategy (parallel or sequential).
    /// Each task is executed through the TaskExecutorMiddleware for error boundary support.
    /// </summary>
    /// <param name="onExecuteTasks">Collection of tasks to be coordinated.</param>
    /// <param name="instanceTransitionId">The instance transition context. Can be null for extension tasks.</param>
    /// <param name="taskTrigger">The trigger type that initiated the task execution.</param>
    /// <param name="context">The script execution context containing instance data and task responses.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    /// <returns>A Result indicating success or failure of the coordination.</returns>
    Task<Result> ExecuteAsync(
        IEnumerable<OnExecuteTask> onExecuteTasks,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken = default);
}
