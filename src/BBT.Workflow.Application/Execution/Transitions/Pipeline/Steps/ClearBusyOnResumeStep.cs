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

        // Only process this step on an internal resume (SubFlow completion or long-poll acknowledge)
        if (!context.Directives.IsInternalResume)
        {
            return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
        }

        // Long-poll acknowledge resume: compare-and-clear the durable ack marker. Acknowledge and
        // the fallback timeout can both request a resume; the reserved ":lpack" lock serializes
        // them, and this guard makes the second one a no-op — if the marker is already cleared the
        // pipeline has already advanced, so stop without re-running the epilogue.
        if (context.Directives.IsLongPollAckResume)
        {
            if (!context.Instance.IsAwaitingLongPollAck)
            {
                return Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Stop()));
            }

            context.Instance.ClearLongPollAck();
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
                // Resume sets the target state here (ChangeStateStep is skipped for resume);
                // re-base any cached ScriptContext snapshot so it reflects the resumed state.
                context.RefreshScriptContextInstance();
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
        // If target state is Busy subtype, ChangeState will handle status. The chain has come
        // to rest in a Busy-subtype state, so release the durable chain-ownership token (the
        // instance stays Busy) — otherwise the chain-token gate would reject legitimate foreign
        // transitions and the ChainReaper would treat the resting instance as stuck.
        if (context.Target?.SubType == StateSubType.Busy)
        {
            context.Directives.RequestEndChain();
            return;
        }

        // Defer status update to after post-commit jobs complete
        if (context.Instance is { IsActive: false, IsCompleted: false })
        {
            context.Directives.SetResolvedStatus(InstanceStatus.Active);
        }
    }
}
