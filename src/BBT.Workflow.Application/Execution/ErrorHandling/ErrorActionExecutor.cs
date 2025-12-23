using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Executes error actions based on resolved policies.
/// Implements Abort, Retry, Rollback, Ignore, Notify, and Log actions.
/// </summary>
public sealed class ErrorActionExecutor : IErrorActionExecutor
{
    private readonly ILogger<ErrorActionExecutor> _logger;

    /// <summary>
    /// Initializes a new instance of ErrorActionExecutor.
    /// </summary>
    public ErrorActionExecutor(ILogger<ErrorActionExecutor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ErrorActionResult> ExecuteAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ResolvedPolicy resolvedPolicy,
        CancellationToken cancellationToken = default)
    {
        var rule = resolvedPolicy.Rule;

        _logger.LogInformation(
            "Executing error action {Action} for {ExceptionType} in instance {InstanceId}, level={Level}",
            rule.Action,
            errorContext.ExceptionTypeName,
            context.InstanceId,
            resolvedPolicy.Level);

        return rule.Action switch
        {
            ErrorAction.Abort => await ExecuteAbortAsync(context, errorContext, rule),
            ErrorAction.Retry => await ExecuteRetryAsync(context, errorContext, rule),
            ErrorAction.Rollback => await ExecuteRollbackAsync(context, errorContext, rule),
            ErrorAction.Ignore => await ExecuteIgnoreAsync(context, errorContext, rule),
            ErrorAction.Notify => await ExecuteNotifyAsync(context, errorContext, rule, cancellationToken),
            ErrorAction.Log => await ExecuteLogAsync(context, errorContext, rule),
            _ => ErrorActionResult.Abort(CreateError(errorContext, "Unknown action"))
        };
    }

    /// <summary>
    /// Executes the Abort action.
    /// Returns Result.Fail or triggers an error transition.
    /// </summary>
    private Task<ErrorActionResult> ExecuteAbortAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ErrorHandlerRule rule)
    {
        _logger.LogWarning(
            "Aborting execution due to error: {ExceptionType} - {Message}",
            errorContext.ExceptionTypeName,
            errorContext.Message);

        var error = CreateError(errorContext, "Execution aborted by error boundary");

        if (!string.IsNullOrEmpty(rule.Transition))
        {
            _logger.LogInformation(
                "Abort will trigger transition to {Transition}",
                rule.Transition);
        }

        return Task.FromResult(ErrorActionResult.Abort(error, rule.Transition));
    }

    /// <summary>
    /// Executes the Retry action.
    /// Calculates delay, increments attempt, and signals for retry.
    /// </summary>
    private Task<ErrorActionResult> ExecuteRetryAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ErrorHandlerRule rule)
    {
        if (rule.RetryPolicy == null)
        {
            _logger.LogWarning(
                "Retry action requested but no retry policy configured, aborting");
            return Task.FromResult(ErrorActionResult.Abort(
                CreateError(errorContext, "Retry requested but no policy configured")));
        }

        var taskKey = errorContext.TaskKey ?? "unknown";
        var retryStateKey = RetryState.TaskContextKey(taskKey);

        // Get or create retry state
        if (!context.Items.TryGetValue(retryStateKey, out var stateObj) || stateObj is not RetryState retryState)
        {
            retryState = RetryState.Create(taskKey, rule.RetryPolicy.MaxRetries);
            context.Items[retryStateKey] = retryState;
        }

        // Check if we can retry
        if (!retryState.CanRetry)
        {
            _logger.LogWarning(
                "Max retries ({MaxRetries}) exceeded for task {TaskKey}",
                rule.RetryPolicy.MaxRetries,
                taskKey);

            return Task.FromResult(ErrorActionResult.RetryExhausted(
                CreateError(errorContext, $"Max retries ({rule.RetryPolicy.MaxRetries}) exceeded")));
        }

        // Calculate delay
        var delay = rule.RetryPolicy.CalculateDelay(retryState.Attempt);
        retryState.NextDelay = delay;
        retryState.LastError = errorContext.Message;
        retryState.IncrementAttempt();

        _logger.LogInformation(
            "Scheduling retry {Attempt}/{MaxRetries} for task {TaskKey} after {Delay}ms",
            retryState.Attempt,
            rule.RetryPolicy.MaxRetries,
            taskKey,
            delay.TotalMilliseconds);

        return Task.FromResult(ErrorActionResult.Retry(delay));
    }

    /// <summary>
    /// Executes the Rollback action.
    /// Triggers a compensation transition.
    /// </summary>
    private Task<ErrorActionResult> ExecuteRollbackAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ErrorHandlerRule rule)
    {
        if (string.IsNullOrEmpty(rule.Transition))
        {
            _logger.LogWarning(
                "Rollback action requested but no transition configured, aborting");
            return Task.FromResult(ErrorActionResult.Abort(
                CreateError(errorContext, "Rollback requested but no transition configured")));
        }

        _logger.LogInformation(
            "Triggering rollback transition to {Transition}",
            rule.Transition);

        return Task.FromResult(ErrorActionResult.Transition(rule.Transition));
    }

    /// <summary>
    /// Executes the Ignore action.
    /// Logs the error and continues execution.
    /// </summary>
    private Task<ErrorActionResult> ExecuteIgnoreAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ErrorHandlerRule rule)
    {
        if (rule.LogOnly)
        {
            _logger.LogWarning(
                "Error ignored (log-only): {ExceptionType} - {Message}",
                errorContext.ExceptionTypeName,
                errorContext.Message);
        }
        else
        {
            _logger.LogInformation(
                "Error ignored and continuing: {ExceptionType} - {Message}",
                errorContext.ExceptionTypeName,
                errorContext.Message);
        }

        return Task.FromResult(ErrorActionResult.Continue());
    }

    /// <summary>
    /// Executes the Notify action.
    /// Sends notification and optionally transitions to manual review state.
    /// </summary>
    private async Task<ErrorActionResult> ExecuteNotifyAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ErrorHandlerRule rule,
        CancellationToken cancellationToken)
    {
        var config = rule.NotificationConfig;

        _logger.LogInformation(
            "Sending error notification via {Channel} for {ExceptionType}",
            config?.Channel ?? "default",
            errorContext.ExceptionTypeName);

        // In a real implementation, this would send the notification via a notification service
        // For now, we log the notification details
        if (config != null)
        {
            _logger.LogDebug(
                "Notification details: channel={Channel}, recipients={Recipients}, priority={Priority}",
                config.Channel,
                string.Join(",", config.Recipients),
                config.Priority);
        }

        // If allowed actions are specified, this is a human-in-the-loop scenario
        if (rule.AllowedActions?.Any() == true)
        {
            _logger.LogInformation(
                "Human-in-the-loop review required. Allowed actions: {Actions}",
                string.Join(", ", rule.AllowedActions));
        }

        await Task.CompletedTask; // Placeholder for actual notification send

        return ErrorActionResult.Notified(rule.Transition);
    }

    /// <summary>
    /// Executes the Log action.
    /// Logs the error and continues (equivalent to Ignore with logOnly=true).
    /// </summary>
    private Task<ErrorActionResult> ExecuteLogAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ErrorHandlerRule rule)
    {
        _logger.LogWarning(
            "Error logged (log-only action): {ExceptionType} - {Message}, Details: {Details}",
            errorContext.ExceptionTypeName,
            errorContext.Message,
            errorContext.Details);

        return Task.FromResult(ErrorActionResult.Continue());
    }

    /// <summary>
    /// Creates an Error from an ErrorContext.
    /// </summary>
    private static Error CreateError(ErrorContext errorContext, string actionMessage)
    {
        var code = $"ErrorBoundary.{errorContext.ExceptionTypeName}";
        var message = $"{actionMessage}: {errorContext.Message}";

        return Error.Failure(code, message, errorContext.Details);
    }
}

