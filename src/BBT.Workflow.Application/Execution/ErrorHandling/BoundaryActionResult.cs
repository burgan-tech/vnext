using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Result of error boundary resolution for task execution.
/// Contains the resolved action and all necessary information for pipeline behavior.
/// </summary>
/// <remarks>
/// Replaces the previous TaskBoundaryResult from Tasks.Executors.ErrorBoundary.
/// Consolidated into Execution/ErrorHandling namespace.
/// </remarks>
public sealed record BoundaryActionResult
{
    /// <summary>
    /// Gets the error action determined by the matched rule.
    /// </summary>
    public ErrorAction Action { get; init; }

    /// <summary>
    /// Gets a value indicating whether the task should be retried.
    /// True when Action is Retry and RetryPolicy is available.
    /// </summary>
    public bool ShouldRetry { get; init; }

    /// <summary>
    /// Gets the retry policy to use (when ShouldRetry is true).
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>
    /// Gets the transition key to trigger (for Abort, Rollback, Notify actions).
    /// </summary>
    public string? TransitionKey { get; init; }

    /// <summary>
    /// Gets a value indicating whether the pipeline should continue execution.
    /// True for Ignore/Log actions.
    /// </summary>
    public bool ShouldContinue { get; init; }

    /// <summary>
    /// Gets the error to propagate (for Abort action without transition).
    /// </summary>
    public Error? PropagatedError { get; init; }

    /// <summary>
    /// Gets the boundary level that resolved the policy.
    /// </summary>
    public ErrorBoundaryLevel? ResolvedAtLevel { get; init; }

    /// <summary>
    /// Gets the execution error details.
    /// </summary>
    public ExecutionError? ExecutionError { get; init; }

    /// <summary>
    /// Creates a result indicating pipeline should continue (Ignore/Log).
    /// </summary>
    public static BoundaryActionResult Continue(ErrorAction action, ErrorBoundaryLevel? level) => new()
    {
        Action = action,
        ShouldContinue = true,
        ResolvedAtLevel = level
    };

    /// <summary>
    /// Creates a result indicating retry is needed.
    /// </summary>
    public static BoundaryActionResult Retry(RetryPolicy policy, ErrorBoundaryLevel? level, ExecutionError? error = null) => new()
    {
        Action = ErrorAction.Retry,
        ShouldRetry = true,
        RetryPolicy = policy,
        ResolvedAtLevel = level,
        ExecutionError = error
    };

    /// <summary>
    /// Creates a result indicating a transition should be triggered.
    /// </summary>
    public static BoundaryActionResult Transition(
        string transitionKey,
        ErrorAction action,
        ErrorBoundaryLevel? level,
        ExecutionError? error = null) => new()
    {
        Action = action,
        TransitionKey = transitionKey,
        ShouldContinue = false,
        ResolvedAtLevel = level,
        ExecutionError = error
    };

    /// <summary>
    /// Creates a result indicating abort with error propagation.
    /// </summary>
    public static BoundaryActionResult Abort(Error? error, ErrorBoundaryLevel? level, ExecutionError? executionError = null) => new()
    {
        Action = ErrorAction.Abort,
        ShouldContinue = false,
        PropagatedError = error,
        ResolvedAtLevel = level,
        ExecutionError = executionError
    };

    /// <summary>
    /// Creates a result for unhandled errors.
    /// </summary>
    public static BoundaryActionResult Unhandled(Error error, ExecutionError? executionError = null) => new()
    {
        Action = ErrorAction.Abort,
        ShouldContinue = false,
        PropagatedError = error,
        ResolvedAtLevel = null,
        ExecutionError = executionError
    };
}

