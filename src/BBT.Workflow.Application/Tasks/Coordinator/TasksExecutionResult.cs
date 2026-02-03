using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Result of task(s) execution with detailed error boundary context.
/// Provides comprehensive information about task execution for policy resolution.
/// </summary>
public sealed record TasksExecutionResult
{
    /// <summary>
    /// Gets a value indicating whether all tasks executed successfully.
    /// When true, no error boundary blocking action is required.
    /// Note: IsSuccess can be true even if HasFailedTasks is true (non-blocking failures).
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Gets a value indicating whether any task failed at business level.
    /// These are tasks with BusinessStatus.Failed but no blocking error policy.
    /// Pipeline continues but failures are tracked.
    /// </summary>
    public bool HasFailedTasks { get; init; }

    /// <summary>
    /// Gets the keys of tasks that failed at business level but didn't block pipeline.
    /// </summary>
    public IReadOnlyList<string> FailedTaskKeys { get; init; } = [];

    /// <summary>
    /// Gets the OnExecuteTask that failed and requires error boundary action (if any).
    /// Only set when IsSuccess is false.
    /// </summary>
    public OnExecuteTask? FailedTask { get; init; }

    /// <summary>
    /// Gets the task execution error details for Error Boundary (if any).
    /// Only set when IsSuccess is false.
    /// Uses consolidated ExecutionError from Execution/ErrorHandling.
    /// </summary>
    public ExecutionError? TaskError { get; init; }

    /// <summary>
    /// Gets the boundary action result from error boundary resolution.
    /// Contains the action to take (Abort, Retry, Rollback, Notify, etc.) and transition key.
    /// Set when a task fails and error boundary resolves a policy.
    /// Uses consolidated BoundaryActionResult from Execution/ErrorHandling.
    /// </summary>
    public BoundaryActionResult? BoundaryAction { get; init; }

    /// <summary>
    /// Gets the list of executed tasks with their summaries.
    /// </summary>
    public IReadOnlyList<TaskExecutionSummary> ExecutedTasks { get; init; } = [];

    /// <summary>
    /// Gets the total execution duration in milliseconds.
    /// </summary>
    public long TotalExecutionDurationMs { get; init; }

    /// <summary>
    /// Creates a successful result with no errors.
    /// </summary>
    /// <param name="executedTasks">The list of successfully executed tasks.</param>
    /// <param name="totalDurationMs">The total execution duration.</param>
    /// <returns>A successful TasksExecutionResult.</returns>
    public static TasksExecutionResult Success(
        IReadOnlyList<TaskExecutionSummary>? executedTasks = null,
        long totalDurationMs = 0)
    {
        return new TasksExecutionResult
        {
            IsSuccess = true,
            HasFailedTasks = false,
            FailedTaskKeys = [],
            FailedTask = null,
            TaskError = null,
            ExecutedTasks = executedTasks ?? [],
            TotalExecutionDurationMs = totalDurationMs
        };
    }

    /// <summary>
    /// Creates a result where pipeline continues but some tasks failed at business level.
    /// Used when tasks have BusinessStatus.Failed but no blocking error policy is defined.
    /// </summary>
    /// <param name="executedTasks">The list of executed tasks (including failed ones).</param>
    /// <param name="totalDurationMs">The total execution duration.</param>
    /// <returns>A TasksExecutionResult with IsSuccess=true but HasFailedTasks=true.</returns>
    public static TasksExecutionResult SuccessWithFailedTasks(
        IReadOnlyList<TaskExecutionSummary> executedTasks,
        long totalDurationMs = 0)
    {
        var failedKeys = executedTasks
            .Where(t => !t.IsSuccess)
            .Select(t => t.TaskKey)
            .ToList();

        return new TasksExecutionResult
        {
            IsSuccess = false,
            HasFailedTasks = failedKeys.Count > 0,
            FailedTaskKeys = failedKeys,
            FailedTask = null,
            TaskError = null,
            ExecutedTasks = executedTasks,
            TotalExecutionDurationMs = totalDurationMs
        };
    }

    /// <summary>
    /// Creates a failed result with task error details.
    /// Used when a blocking error policy (Abort/Retry) is triggered.
    /// </summary>
    /// <param name="failedTask">The OnExecuteTask that failed.</param>
    /// <param name="taskError">The task execution error details.</param>
    /// <param name="executedTasks">Tasks that executed before the failure.</param>
    /// <param name="totalDurationMs">The total execution duration.</param>
    /// <returns>A failed TasksExecutionResult.</returns>
    public static TasksExecutionResult Failure(
        OnExecuteTask failedTask,
        ExecutionError taskError,
        IReadOnlyList<TaskExecutionSummary>? executedTasks = null,
        long totalDurationMs = 0)
    {
        return new TasksExecutionResult
        {
            IsSuccess = false,
            HasFailedTasks = true,
            FailedTaskKeys = [failedTask.Task.Key],
            FailedTask = failedTask,
            TaskError = taskError,
            ExecutedTasks = executedTasks ?? [],
            TotalExecutionDurationMs = totalDurationMs
        };
    }

    /// <summary>
    /// Creates a result with a boundary action from error boundary resolution.
    /// Used when error boundary resolves a policy with an action (Abort, Rollback, Notify with transition).
    /// </summary>
    /// <param name="failedTask">The OnExecuteTask that failed.</param>
    /// <param name="boundaryResult">The boundary resolution result with action details.</param>
    /// <param name="executedTasks">Tasks that executed before the failure.</param>
    /// <param name="totalDurationMs">The total execution duration.</param>
    /// <returns>A TasksExecutionResult with boundary action.</returns>
    public static TasksExecutionResult WithBoundaryAction(
        OnExecuteTask failedTask,
        BoundaryActionResult boundaryResult,
        IReadOnlyList<TaskExecutionSummary>? executedTasks = null,
        long totalDurationMs = 0)
    {
        return new TasksExecutionResult
        {
            IsSuccess = false,
            HasFailedTasks = true,
            FailedTaskKeys = [failedTask.Task.Key],
            FailedTask = failedTask,
            TaskError = boundaryResult.ExecutionError,
            BoundaryAction = boundaryResult,
            ExecutedTasks = executedTasks ?? [],
            TotalExecutionDurationMs = totalDurationMs
        };
    }
}

/// <summary>
/// Summary of a single task execution within a batch.
/// </summary>
public sealed record TaskExecutionSummary
{
    /// <summary>
    /// Gets the key of the executed task.
    /// </summary>
    public required string TaskKey { get; init; }

    /// <summary>
    /// Gets the type of task that was executed.
    /// </summary>
    public required string TaskType { get; init; }

    /// <summary>
    /// Gets a value indicating whether the task executed successfully.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Gets the HTTP status code if applicable.
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Gets the execution duration in milliseconds.
    /// </summary>
    public long ExecutionDurationMs { get; init; }

    /// <summary>
    /// Gets the error message if the task failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Creates a successful task execution summary.
    /// </summary>
    public static TaskExecutionSummary Success(
        string taskKey,
        string taskType,
        int? statusCode = null,
        long executionDurationMs = 0)
    {
        return new TaskExecutionSummary
        {
            TaskKey = taskKey,
            TaskType = taskType,
            IsSuccess = true,
            StatusCode = statusCode,
            ExecutionDurationMs = executionDurationMs
        };
    }

    /// <summary>
    /// Creates a failed task execution summary.
    /// </summary>
    public static TaskExecutionSummary Failure(
        string taskKey,
        string taskType,
        string? errorMessage,
        int? statusCode = null,
        long executionDurationMs = 0)
    {
        return new TaskExecutionSummary
        {
            TaskKey = taskKey,
            TaskType = taskType,
            IsSuccess = false,
            StatusCode = statusCode,
            ExecutionDurationMs = executionDurationMs,
            ErrorMessage = errorMessage
        };
    }
}
