using System.Diagnostics;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Null implementation of IDisposable for null scope handling.
/// </summary>
internal sealed class NullScope : IDisposable
{
    public static NullScope Instance { get; } = new();

    private NullScope() { }

    public void Dispose() { }
}

/// <summary>
/// Telemetry helper for error boundary operations.
/// Provides structured logging and metrics for error resolution and action execution.
/// </summary>
public sealed class ErrorBoundaryTelemetry : IErrorBoundaryTelemetry
{
    private readonly IWorkflowMetrics _metrics;
    private readonly ILogger<ErrorBoundaryTelemetry> _logger;

    /// <summary>
    /// Initializes a new instance of ErrorBoundaryTelemetry.
    /// </summary>
    public ErrorBoundaryTelemetry(
        IWorkflowMetrics metrics,
        ILogger<ErrorBoundaryTelemetry> logger)
    {
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public void RecordPolicyResolution(
        string workflow,
        ResolvedPolicy? resolvedPolicy,
        ErrorContext errorContext)
    {
        if (resolvedPolicy != null)
        {
            _metrics.RecordErrorBoundaryResolution(
                workflow,
                resolvedPolicy.Level.ToString().ToLowerInvariant(),
                resolvedPolicy.Rule.Action.ToString().ToLowerInvariant());

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                ["boundaryLevel"] = resolvedPolicy.Level.ToString(),
                ["selectedRuleAction"] = resolvedPolicy.Rule.Action.ToString(),
                ["selectedRulePriority"] = resolvedPolicy.EffectivePriority,
                ["exceptionType"] = errorContext.ExceptionTypeName,
                ["errorCode"] = errorContext.ErrorCode?.ToString() ?? "none",
                ["isTimeout"] = errorContext.IsTimeout,
                ["retryAttempt"] = errorContext.Attempt,
                ["workflow"] = workflow,
                ["instanceId"] = errorContext.InstanceId.ToString()
            }))
            {
                _logger.LogInformation(
                    "Error policy resolved: {Action} at {Level} level for {ExceptionType}",
                    resolvedPolicy.Rule.Action,
                    resolvedPolicy.Level,
                    errorContext.ExceptionTypeName);
            }
        }
        else
        {
            _metrics.RecordErrorBoundaryUnhandled(
                workflow,
                errorContext.ExceptionTypeName,
                errorContext.Scope.ToString().ToLowerInvariant());

            _logger.LogWarning(
                "No error policy found for {ExceptionType} in {Scope} scope",
                errorContext.ExceptionTypeName,
                errorContext.Scope);
        }
    }

    /// <inheritdoc />
    public void RecordRetryAttempt(
        string workflow,
        string taskKey,
        string taskType,
        int attempt,
        TimeSpan delay)
    {
        _metrics.RecordErrorBoundaryRetry(workflow, taskType, attempt);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["taskKey"] = taskKey,
            ["taskType"] = taskType,
            ["retryAttempt"] = attempt,
            ["nextDelay"] = delay.TotalMilliseconds,
            ["workflow"] = workflow
        }))
        {
            _logger.LogInformation(
                "Retry scheduled: attempt {Attempt} for {TaskKey} after {DelayMs}ms",
                attempt,
                taskKey,
                delay.TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public void RecordActionExecution(
        ErrorAction action,
        ErrorContext errorContext,
        ErrorActionResult result,
        TimeSpan duration)
    {
        _metrics.RecordErrorActionDuration(
            action.ToString().ToLowerInvariant(),
            duration.TotalSeconds);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["action"] = action.ToString(),
            ["shouldContinue"] = result.ShouldContinue,
            ["shouldRetry"] = result.ShouldRetry,
            ["hasTransition"] = result.HasTransition,
            ["chosenTransition"] = result.TransitionKey ?? "none",
            ["durationMs"] = duration.TotalMilliseconds,
            ["exceptionType"] = errorContext.ExceptionTypeName,
            ["instanceId"] = errorContext.InstanceId.ToString()
        }))
        {
            _logger.LogInformation(
                "Error action executed: {Action} with outcome continue={Continue}, retry={Retry}, transition={Transition}",
                action,
                result.ShouldContinue,
                result.ShouldRetry,
                result.TransitionKey ?? "none");
        }
    }

    /// <inheritdoc />
    public void RecordSubFlowErrorPropagation(
        string parentWorkflow,
        string childWorkflow,
        bool propagated,
        ErrorContext errorContext)
    {
        _metrics.RecordSubFlowErrorPropagation(parentWorkflow, childWorkflow, propagated);

        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["parentWorkflow"] = parentWorkflow,
            ["childWorkflow"] = childWorkflow,
            ["propagated"] = propagated,
            ["exceptionType"] = errorContext.ExceptionTypeName,
            ["instanceId"] = errorContext.InstanceId.ToString()
        }))
        {
            if (propagated)
            {
                _logger.LogInformation(
                    "SubFlow error propagated to parent: {ExceptionType} from {ChildWorkflow} to {ParentWorkflow}",
                    errorContext.ExceptionTypeName,
                    childWorkflow,
                    parentWorkflow);
            }
            else
            {
                _logger.LogInformation(
                    "SubFlow error contained (not propagated): {ExceptionType} in {ChildWorkflow}",
                    errorContext.ExceptionTypeName,
                    childWorkflow);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable BeginErrorBoundaryScope(ErrorContext errorContext)
    {
        return _logger.BeginScope(new Dictionary<string, object>
        {
            ["errorBoundary.scope"] = errorContext.Scope.ToString(),
            ["errorBoundary.exceptionType"] = errorContext.ExceptionTypeName,
            ["errorBoundary.errorCode"] = errorContext.ErrorCode?.ToString() ?? "none",
            ["errorBoundary.isTimeout"] = errorContext.IsTimeout,
            ["errorBoundary.taskKey"] = errorContext.TaskKey ?? "none",
            ["errorBoundary.stateKey"] = errorContext.StateKey ?? "none",
            ["errorBoundary.instanceId"] = errorContext.InstanceId.ToString(),
            ["errorBoundary.domain"] = errorContext.Domain,
            ["errorBoundary.flow"] = errorContext.Flow
        }) ?? NullScope.Instance;
    }

    /// <inheritdoc />
    public Activity? StartErrorBoundaryActivity(string operationName, ErrorContext errorContext)
    {
        var activity = Activity.Current?.Source.StartActivity(operationName);

        if (activity != null)
        {
            activity.SetTag("error.boundary.scope", errorContext.Scope.ToString());
            activity.SetTag("error.boundary.exception_type", errorContext.ExceptionTypeName);
            activity.SetTag("error.boundary.error_code", errorContext.ErrorCode?.ToString() ?? "none");
            activity.SetTag("error.boundary.is_timeout", errorContext.IsTimeout);
            activity.SetTag("error.boundary.task_key", errorContext.TaskKey ?? "none");
            activity.SetTag("error.boundary.state_key", errorContext.StateKey ?? "none");
            activity.SetTag("workflow.instance_id", errorContext.InstanceId.ToString());
            activity.SetTag("workflow.domain", errorContext.Domain);
            activity.SetTag("workflow.flow", errorContext.Flow);
        }

        return activity;
    }
}

/// <summary>
/// Interface for error boundary telemetry operations.
/// </summary>
public interface IErrorBoundaryTelemetry
{
    /// <summary>
    /// Records error policy resolution.
    /// </summary>
    void RecordPolicyResolution(string workflow, ResolvedPolicy? resolvedPolicy, ErrorContext errorContext);

    /// <summary>
    /// Records a retry attempt.
    /// </summary>
    void RecordRetryAttempt(string workflow, string taskKey, string taskType, int attempt, TimeSpan delay);

    /// <summary>
    /// Records error action execution.
    /// </summary>
    void RecordActionExecution(ErrorAction action, ErrorContext errorContext, ErrorActionResult result, TimeSpan duration);

    /// <summary>
    /// Records SubFlow error propagation.
    /// </summary>
    void RecordSubFlowErrorPropagation(string parentWorkflow, string childWorkflow, bool propagated, ErrorContext errorContext);

    /// <summary>
    /// Begins a logging scope for error boundary operations.
    /// </summary>
    IDisposable BeginErrorBoundaryScope(ErrorContext errorContext);

    /// <summary>
    /// Starts an activity for distributed tracing.
    /// </summary>
    Activity? StartErrorBoundaryActivity(string operationName, ErrorContext errorContext);
}

