using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Engine for executing single tasks with full lifecycle management.
/// Single responsibility: execute one task with error boundary integration.
/// </summary>
/// <remarks>
/// Extracted from TaskCoordinator to follow SRP.
/// TaskCoordinator orchestrates multiple tasks, this engine executes individual tasks.
/// </remarks>
public interface ITaskExecutionEngine
{
    /// <summary>
    /// Executes a single task with full lifecycle: factory creation, execution,
    /// error boundary resolution, persistence, and metrics.
    /// </summary>
    /// <param name="onExecuteTask">The task configuration to execute.</param>
    /// <param name="instanceTransitionId">The instance transition context.</param>
    /// <param name="taskTrigger">The trigger type that initiated execution.</param>
    /// <param name="context">The script context containing instance data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing execution result with error boundary info.</returns>
    Task<Result<TasksExecutionResult>> ExecuteAsync(
        OnExecuteTask onExecuteTask,
        Guid? instanceTransitionId,
        TaskTrigger taskTrigger,
        ScriptContext context,
        CancellationToken cancellationToken);
}

