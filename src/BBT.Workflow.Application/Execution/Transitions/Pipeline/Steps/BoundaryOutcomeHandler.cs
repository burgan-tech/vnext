using BBT.Aether.Results;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Shared handler for converting boundary action results to step outcomes.
/// Used by task steps (OnExecute, OnEntry, OnExit) to handle error boundary actions consistently.
/// </summary>
/// <remarks>
/// Follows DRY principle by centralizing boundary action handling logic.
/// Each action type maps to a specific pipeline behavior:
/// - Log/Ignore: Pipeline continues execution (incident created as already-resolved)
/// - Abort/Notify/Rollback with transition: Sets error transition and skips to Finalize
/// - Abort/Unhandled without transition: Returns Fail so instance is marked Faulted
/// </remarks>
/// <remarks>
/// <b>The caller persists what this records.</b> Every method here only mutates the aggregate; the
/// step MUST save immediately afterwards. The pipeline's fault path reloads the instance in its own
/// unit of work and skips its fallback incident only when the committed <c>HasActiveIncident</c>
/// column says one exists — recording without saving is what made an abort produce two incidents.
/// </remarks>
public static class BoundaryOutcomeHandler
{
    /// <summary>
    /// Handles a boundary action result and returns the appropriate step outcome.
    /// Records an <see cref="InstanceIncident"/> on the instance for all non-continue actions.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="result">The task execution result containing boundary action.</param>
    /// <returns>A Result containing the step outcome based on the boundary action.</returns>
    public static Result<StepOutcome> Handle(
        TransitionExecutionContext context,
        TasksExecutionResult result,
        ILogger logger)
    {
        var action = result.BoundaryAction;
        if (action == null)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Log/Ignore - continue pipeline execution (record informational incident as already-resolved).
        //
        // UNREACHABLE from the task steps as the engine stands: a continue-style outcome comes back as
        // TasksExecutionResult.SuccessWithFailedTasks, which carries NO boundary action, so the step's
        // BoundaryAction guard is false and this method is never called for Log/Ignore. The measured
        // behaviour is therefore "no incident, and the rest of the hook is skipped". Left in place so
        // the intended semantics are still expressed if the engine starts attaching the action.
        if (action.ShouldContinue)
        {
            var resolvedIncident = BuildIncident(context, result);
            resolvedIncident.Resolve();
            context.Instance.AddIncident(resolvedIncident);
            logger.IncidentRecordedInformational(
                context.Instance.Id, resolvedIncident.ErrorCode, resolvedIncident.BoundaryAction);
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        RecordIncident(context, result, logger);

        // Abort/Notify/Rollback with transition - request next transition and skip to finalize
        if (!string.IsNullOrEmpty(action.TransitionKey))
        {
            context.Directives.RequestNextTransition(
                new NextTransitionRequest(action.TransitionKey, TransitionRequestReasons.ErrorBoundary));
            return Result<StepOutcome>.Ok(StepOutcome.SkipToFinalize());
        }

        // Abort/Unhandled without transition - return Fail so instance is marked Faulted
        return Result<StepOutcome>.Fail(
            action.PropagatedError ?? Error.Failure("ErrorBoundaryAbort", "Error boundary aborted."));
    }

    /// <summary>
    /// Records an active incident on the instance from the boundary action context.
    /// </summary>
    internal static void RecordIncident(
        TransitionExecutionContext context,
        TasksExecutionResult result,
        ILogger logger)
    {
        var incident = BuildIncident(context, result);
        context.Instance.AddIncident(incident);
        logger.IncidentRecorded(
            context.Instance.Id, incident.State, incident.Transition, incident.ErrorCode, incident.BoundaryAction);
    }

    /// <summary>
    /// Records an incident directly from an <see cref="ExecutionError"/> (used by task steps
    /// for unhandled failures that bypass boundary resolution).
    /// </summary>
    internal static void RecordUnhandledIncident(
        TransitionExecutionContext context,
        ExecutionError executionError,
        ILogger logger)
    {
        var incident = InstanceIncidentFactory.Create(
            state: context.Instance.GetCurrentState,
            transition: context.TransitionKey,
            taskKey: executionError.TaskKey,
            message: executionError.ErrorMessage ?? "Unhandled task execution error",
            errorCode: executionError.NormalizedError.Code,
            errorLayer: executionError.NormalizedError.Layer.ToString(),
            statusCode: executionError.StatusCode,
            stackTrace: executionError.StackTrace,
            traceId: context.TraceId);

        context.Instance.AddIncident(incident);
        logger.IncidentRecorded(
            context.Instance.Id, incident.State, incident.Transition, incident.ErrorCode, incident.BoundaryAction);
    }

    private static InstanceIncident BuildIncident(
        TransitionExecutionContext context,
        TasksExecutionResult result)
    {
        var action = result.BoundaryAction;
        var error = result.TaskError ?? action?.ExecutionError;

        return InstanceIncidentFactory.Create(
            state: context.Instance.GetCurrentState,
            transition: context.TransitionKey,
            taskKey: error?.TaskKey ?? result.FailedTask?.Task.Key,
            message: error?.ErrorMessage ?? action?.PropagatedError?.Message ?? "Error boundary triggered",
            errorCode: error?.NormalizedError.Code ?? action?.PropagatedError?.Code ?? "Unknown",
            errorLayer: error?.NormalizedError.Layer.ToString() ?? "Pipeline",
            statusCode: error?.StatusCode,
            stackTrace: error?.StackTrace,
            boundaryAction: action?.Action.ToString(),
            boundaryLevel: action?.ResolvedAtLevel?.ToString(),
            retryCount: action?.RetryPolicy?.MaxRetries ?? 0,
            traceId: context.TraceId);
    }
}
