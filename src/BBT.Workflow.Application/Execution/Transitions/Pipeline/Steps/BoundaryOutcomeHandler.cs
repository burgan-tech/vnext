using BBT.Aether.Results;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Tasks.Coordinator;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Shared handler for converting boundary action results to step outcomes.
/// Used by task steps (OnExecute, OnEntry, OnExit) to handle error boundary actions consistently.
/// </summary>
/// <remarks>
/// Follows DRY principle by centralizing boundary action handling logic.
/// Each action type maps to a specific pipeline behavior:
/// - Log/Ignore: Pipeline continues execution
/// - Abort/Notify/Rollback with transition: Sets error transition and skips to Finalize
/// - Abort without transition: Requests boundary abort and skips to Finalize (no fault)
/// </remarks>
public static class BoundaryOutcomeHandler
{
    /// <summary>
    /// Handles a boundary action result and returns the appropriate step outcome.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="result">The task execution result containing boundary action.</param>
    /// <returns>A Result containing the step outcome based on the boundary action.</returns>
    public static Result<StepOutcome> Handle(
        TransitionExecutionContext context,
        TasksExecutionResult result)
    {
        var action = result.BoundaryAction;
        if (action == null)
        {
            // No boundary action - this should not happen, but return continue as safe default
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Log/Ignore - continue pipeline execution
        if (action.ShouldContinue)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Abort/Notify/Rollback with transition - request next transition and skip to finalize
        // Current pipeline will complete (finalize), then the requested transition will start its own pipeline
        if (!string.IsNullOrEmpty(action.TransitionKey))
        {
            context.Directives.RequestNextTransition(
                new NextTransitionRequest(action.TransitionKey, "error_boundary"));
            return Result<StepOutcome>.Ok(StepOutcome.SkipToFinalize());
        }

        // Abort without transition - request boundary abort and skip to finalize
        // Pipeline will stop but NOT mark instance as faulted
        context.Directives.RequestBoundaryAbort();
        return Result<StepOutcome>.Ok(StepOutcome.SkipToFinalize());
    }
}
