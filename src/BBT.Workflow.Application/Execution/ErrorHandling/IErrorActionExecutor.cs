using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Executes error boundary actions based on matched rules.
/// Single responsibility: convert boundary resolution into execution result.
/// </summary>
/// <remarks>
/// Action behaviors:
/// - Abort: Finalize current loop, return current state to client (not faulted)
/// - Notify/Rollback: If transition specified, finalize and request transition; else behave like Abort
/// - Log: Log error only, continue pipeline lifecycle
/// - Ignore: Low-level log, continue pipeline, ignore failure
/// - Retry: Execute Polly policy, default 0 attempts if no policy defined
/// </remarks>
public interface IErrorActionExecutor
{
    /// <summary>
    /// Executes the action from a boundary resolution result.
    /// </summary>
    /// <param name="resolution">The boundary resolution result containing the matched rule.</param>
    /// <param name="error">The execution error details.</param>
    /// <param name="retryExecutor">Function to execute retry with Polly policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The action execution result indicating pipeline behavior.</returns>
    Task<ActionExecutionResult> ExecuteAsync(
        BoundaryResolutionResult resolution,
        ExecutionError error,
        Func<RetryPolicy, CancellationToken, Task<Result<ActionExecutionResult>>>? retryExecutor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of error action execution.
/// Indicates how the pipeline should proceed after error handling.
/// </summary>
public sealed record ActionExecutionResult
{
    /// <summary>
    /// Gets a value indicating whether the pipeline should continue execution.
    /// True for Ignore/Log actions or successful retry.
    /// </summary>
    public bool ShouldContinue { get; init; }

    /// <summary>
    /// Gets a value indicating whether a retry was attempted and succeeded.
    /// </summary>
    public bool RetrySucceeded { get; init; }

    /// <summary>
    /// Gets the transition key to trigger, if any.
    /// Set for Abort/Rollback/Notify actions with configured transition.
    /// </summary>
    public string? TransitionKey { get; init; }

    /// <summary>
    /// Gets the action that was executed.
    /// </summary>
    public ErrorAction ExecutedAction { get; init; }

    /// <summary>
    /// Gets the level at which the boundary was resolved.
    /// </summary>
    public ErrorBoundaryLevel? ResolvedAtLevel { get; init; }

    /// <summary>
    /// Gets the error to propagate if pipeline should stop.
    /// </summary>
    public Error? PropagatedError { get; init; }

    /// <summary>
    /// Gets the execution error details.
    /// </summary>
    public ExecutionError? ExecutionError { get; init; }

    /// <summary>
    /// Creates a result indicating pipeline should continue (Ignore/Log).
    /// </summary>
    public static ActionExecutionResult Continue(ErrorAction action, ErrorBoundaryLevel? level) => new()
    {
        ShouldContinue = true,
        RetrySucceeded = false,
        ExecutedAction = action,
        ResolvedAtLevel = level
    };

    /// <summary>
    /// Creates a result indicating retry succeeded.
    /// </summary>
    public static ActionExecutionResult RetrySuccess(ErrorBoundaryLevel? level) => new()
    {
        ShouldContinue = true,
        RetrySucceeded = true,
        ExecutedAction = ErrorAction.Retry,
        ResolvedAtLevel = level
    };

    /// <summary>
    /// Creates a result indicating a transition should be triggered.
    /// </summary>
    public static ActionExecutionResult Transition(
        string transitionKey,
        ErrorAction action,
        ErrorBoundaryLevel? level,
        ExecutionError? error = null) => new()
    {
        ShouldContinue = false,
        TransitionKey = transitionKey,
        ExecutedAction = action,
        ResolvedAtLevel = level,
        ExecutionError = error
    };

    /// <summary>
    /// Creates a result indicating pipeline should abort.
    /// </summary>
    public static ActionExecutionResult Abort(
        Error? error,
        ErrorBoundaryLevel? level,
        ExecutionError? executionError = null) => new()
    {
        ShouldContinue = false,
        ExecutedAction = ErrorAction.Abort,
        PropagatedError = error,
        ResolvedAtLevel = level,
        ExecutionError = executionError
    };

    /// <summary>
    /// Creates a result for unhandled errors.
    /// </summary>
    public static ActionExecutionResult Unhandled(Error error, ExecutionError? executionError = null) => new()
    {
        ShouldContinue = false,
        ExecutedAction = ErrorAction.Abort,
        PropagatedError = error,
        ResolvedAtLevel = null,
        ExecutionError = executionError
    };
}

