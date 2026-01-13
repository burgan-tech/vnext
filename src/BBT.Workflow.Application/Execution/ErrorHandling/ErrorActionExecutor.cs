using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Executes error boundary actions based on matched rules.
/// Handles all action types: Abort, Retry, Rollback, Notify, Log, Ignore.
/// </summary>
/// <remarks>
/// Action behaviors:
/// - Abort: Finalize current loop, return current state to client (not faulted)
/// - Notify/Rollback: If transition specified, finalize and request transition; else behave like Abort
/// - Log: Log error only, continue pipeline lifecycle
/// - Ignore: Low-level log, continue pipeline, ignore failure
/// - Retry: Execute Polly policy, default 0 attempts if no policy defined
/// </remarks>
public sealed class ErrorActionExecutor : IErrorActionExecutor
{
    private readonly ILogger<ErrorActionExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of the ErrorActionExecutor.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public ErrorActionExecutor(ILogger<ErrorActionExecutor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ActionExecutionResult> ExecuteAsync(
        BoundaryResolutionResult resolution,
        ExecutionError error,
        Func<RetryPolicy, CancellationToken, Task<Result<ActionExecutionResult>>>? retryExecutor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Handle unhandled errors (no boundary matched)
        if (!resolution.IsHandled)
        {
            _logger.LogWarning(
                "No error boundary matched for task {TaskKey}. Propagating error: {ErrorCode}",
                error.TaskKey,
                error.NormalizedError.Code);

            return ActionExecutionResult.Unhandled(error.ToError(), error);
        }

        var action = resolution.Action;
        var level = resolution.ResolvedAtLevel;

        _logger.LogDebug(
            "Executing error action {Action} for task {TaskKey}. Level: {Level}",
            action,
            error.TaskKey,
            level);

        return action switch
        {
            ErrorAction.Retry => await HandleRetryAsync(resolution, error, retryExecutor, cancellationToken),
            ErrorAction.Ignore => HandleIgnore(error, level),
            ErrorAction.Log => HandleLog(error, level),
            ErrorAction.Abort => HandleAbort(resolution, error, level),
            ErrorAction.Rollback => HandleRollbackOrNotify(resolution, error, level, ErrorAction.Rollback),
            ErrorAction.Notify => HandleRollbackOrNotify(resolution, error, level, ErrorAction.Notify),
            _ => HandleAbort(resolution, error, level)
        };
    }

    /// <summary>
    /// Handles Retry action using Polly policy.
    /// </summary>
    private async Task<ActionExecutionResult> HandleRetryAsync(
        BoundaryResolutionResult resolution,
        ExecutionError error,
        Func<RetryPolicy, CancellationToken, Task<Result<ActionExecutionResult>>>? retryExecutor,
        CancellationToken cancellationToken)
    {
        var retryPolicy = resolution.RetryPolicy;

        if (retryPolicy == null || retryPolicy.MaxRetries <= 0)
        {
            _logger.LogWarning(
                "Retry action specified but no retry policy configured for task {TaskKey}. Treating as Abort.",
                error.TaskKey);

            return ActionExecutionResult.Abort(error.ToError(), resolution.ResolvedAtLevel, error);
        }

        if (retryExecutor == null)
        {
            _logger.LogWarning(
                "Retry action specified but no retry executor provided for task {TaskKey}. Treating as Abort.",
                error.TaskKey);

            return ActionExecutionResult.Abort(error.ToError(), resolution.ResolvedAtLevel, error);
        }

        _logger.LogInformation(
            "Executing retry for task {TaskKey}. MaxRetries: {MaxRetries}, BackoffType: {BackoffType}",
            error.TaskKey,
            retryPolicy.MaxRetries,
            retryPolicy.BackoffType);

        var result = await retryExecutor(retryPolicy, cancellationToken);

        if (result is { IsSuccess: true, Value: not null })
        {
            return result.Value;
        }

        // Retry exhausted or failed
        var errorMessage = result.IsSuccess ? "Unknown" : result.Error.Message;
        _logger.LogWarning(
            "Retry exhausted for task {TaskKey}. Error: {Error}",
            error.TaskKey,
            errorMessage ?? "Unknown");

        var propagatedError = result.IsSuccess ? error.ToError() : result.Error;
        return ActionExecutionResult.Abort(
            propagatedError,
            resolution.ResolvedAtLevel,
            error);
    }

    /// <summary>
    /// Handles Ignore action - pipeline continues, failure is ignored.
    /// </summary>
    private ActionExecutionResult HandleIgnore(ExecutionError error, ErrorBoundaryLevel? level)
    {
        _logger.LogDebug(
            "Ignoring error for task {TaskKey}. Error: {ErrorCode}",
            error.TaskKey,
            error.NormalizedError.Code);

        return ActionExecutionResult.Continue(ErrorAction.Ignore, level);
    }

    /// <summary>
    /// Handles Log action - logs error and continues pipeline.
    /// </summary>
    private ActionExecutionResult HandleLog(ExecutionError error, ErrorBoundaryLevel? level)
    {
        _logger.LogWarning(
            "Error boundary logged for task {TaskKey}. Code: {ErrorCode}, Message: {Message}, StatusCode: {StatusCode}",
            error.TaskKey,
            error.NormalizedError.Code,
            error.ErrorMessage,
            error.StatusCode);

        return ActionExecutionResult.Continue(ErrorAction.Log, level);
    }

    /// <summary>
    /// Handles Abort action - finalizes current loop and returns.
    /// If transition is specified, triggers that transition.
    /// </summary>
    private ActionExecutionResult HandleAbort(
        BoundaryResolutionResult resolution,
        ExecutionError error,
        ErrorBoundaryLevel? level)
    {
        var transitionKey = resolution.TransitionKey;

        if (!string.IsNullOrEmpty(transitionKey))
        {
            _logger.LogInformation(
                "Abort with transition for task {TaskKey}. Transition: {Transition}",
                error.TaskKey,
                transitionKey);

            return ActionExecutionResult.Transition(transitionKey, ErrorAction.Abort, level, error);
        }

        _logger.LogInformation(
            "Abort without transition for task {TaskKey}. Finalizing current state.",
            error.TaskKey);

        return ActionExecutionResult.Abort(error.ToError(), level, error);
    }

    /// <summary>
    /// Handles Rollback and Notify actions.
    /// These actions require a transition to be specified.
    /// If no transition, behaves like Abort.
    /// </summary>
    private ActionExecutionResult HandleRollbackOrNotify(
        BoundaryResolutionResult resolution,
        ExecutionError error,
        ErrorBoundaryLevel? level,
        ErrorAction action)
    {
        var transitionKey = resolution.TransitionKey;

        if (string.IsNullOrEmpty(transitionKey))
        {
            _logger.LogWarning(
                "{Action} action specified but no transition configured for task {TaskKey}. Treating as Abort.",
                action,
                error.TaskKey);

            return ActionExecutionResult.Abort(error.ToError(), level, error);
        }

        _logger.LogInformation(
            "{Action} for task {TaskKey}. Transition: {Transition}",
            action,
            error.TaskKey,
            transitionKey);

        return ActionExecutionResult.Transition(transitionKey, action, level, error);
    }
}

