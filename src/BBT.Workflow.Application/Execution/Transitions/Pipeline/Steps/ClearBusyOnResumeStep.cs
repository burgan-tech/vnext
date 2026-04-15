using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that clears the busy state when resuming from SubFlow completion.
/// Implements optimizations:
/// 1. Resolves target state first to check SubType
/// 2. Skips status update if target state is Busy SubType (ChangeState will handle it)
/// 3. Implements idempotency - only updates DB if status actually changes
/// </summary>
public sealed class ClearBusyOnResumeStep() : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ClearBusyOnResumeStep;

    /// <inheritdoc />
    [Trace]
    public Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ClearBusyOnResumeStep)}");

        // Only process this step if resuming from SubFlow completion
        if (!context.Directives.IsSubFlowResume)
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Resolve target state first, then conditionally defer status
        var stateResult = UpdateTargetStateInContext(context);
        if (!stateResult.IsSuccess)
            return Task.FromResult(Result<StepOutcome>.Fail(stateResult.Error));

        ClearBusyIfNeeded(context);
        return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
    }

    /// <summary>
    /// Updates target state in context by resolving the current state from workflow definition.
    /// </summary>
    private static Result<TransitionExecutionContext> UpdateTargetStateInContext(TransitionExecutionContext context)
    {
        return context.Workflow.GetState(context.Instance.GetCurrentState)
            .Map(state =>
            {
                context.Target = state;
                return context;
            });
    }

    /// <summary>
    /// Conditionally defers the Active status update via PipelineDirectives.
    /// Skips if target state SubType is Busy (ChangeState will handle it).
    /// Skips if instance is already Active or Completed (idempotency).
    /// </summary>
    private static void ClearBusyIfNeeded(TransitionExecutionContext context)
    {
        // If target state is Busy subtype, ChangeState will handle status
        if (context.Target?.SubType == StateSubType.Busy)
            return;

        // Defer status update to after post-commit jobs complete
        if (context.Instance is { IsActive: false, IsCompleted: false })
        {
            context.Directives.SetResolvedStatus(InstanceStatus.Active);
        }
    }
}
