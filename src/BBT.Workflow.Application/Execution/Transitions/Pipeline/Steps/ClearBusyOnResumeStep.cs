using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that clears the busy state when resuming from SubFlow completion.
/// Implements optimizations:
/// 1. Resolves target state first to check SubType
/// 2. Skips status update if target state is Busy SubType (ChangeState will handle it)
/// 3. Implements idempotency - only updates DB if status actually changes
/// 4. Stamps the completing correlation's settlement marker in this hop's own unit of work when the
///    completion relay handed the terminal outcome in (<see cref="PipelineDirectives.SubFlowResumeTerminalOutcome"/>),
///    so the relay does not open a separate unit of work for that one write after the resume.
/// </summary>
public sealed class ClearBusyOnResumeStep(
    IInstanceCorrelationRepository correlationRepository,
    ILogger<ClearBusyOnResumeStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ClearBusyOnResumeStep;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Only process this step on an internal resume (SubFlow completion or long-poll acknowledge)
        if (!context.Directives.IsInternalResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Long-poll acknowledge resume: compare-and-clear the durable ack marker. Acknowledge and
        // the fallback timeout can both request a resume; the reserved ":lpack" lock serializes
        // them, and this guard makes the second one a no-op — if the marker is already cleared the
        // pipeline has already advanced, so stop without re-running the epilogue.
        if (context.Directives.IsLongPollAckResume)
        {
            if (!context.Instance.IsAwaitingLongPollAck)
            {
                return Result<StepOutcome>.Ok(StepOutcome.Stop());
            }

            context.Instance.ClearLongPollAck();
        }

        // Resolve target state first, then conditionally defer status
        var stateResult = UpdateTargetStateInContext(context);
        if (!stateResult.IsSuccess)
            return Result<StepOutcome>.Fail(stateResult.Error);

        ClearBusyIfNeeded(context);
        await MarkSubItemSettledAsync(context, cancellationToken);
        return Result<StepOutcome>.Ok(StepOutcome.Continue());
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
        // If target state is Busy subtype, ChangeState will handle status: the instance
        // deliberately rests Busy until an external signal advances it.
        if (context.Target?.SubType == StateSubType.Busy)
        {
            return;
        }

        // Defer status update to after post-commit jobs complete
        if (context.Instance is { IsActive: false, IsCompleted: false })
        {
            context.Directives.SetResolvedStatus(InstanceStatus.Active);
        }
    }

    /// <summary>
    /// Durably marks the completing correlation as settled, in this resume hop's unit of work.
    /// The completion relay used to do this in a separate unit of work after the resume returned —
    /// one more context, connection rental and round trip per completion for a single set-based
    /// UPDATE. Writing it here keeps the marker's meaning (the parent's resume reached the database):
    /// the hop's own commit carries it, and a resume that fails afterwards reverts the correlation,
    /// which clears the marker again (<c>InstanceCorrelation.Revert</c>). Best-effort, as before: the
    /// marker only feeds the duplicate-delivery fast path, so a failed write must not fail the resume.
    /// </summary>
    private async Task MarkSubItemSettledAsync(TransitionExecutionContext context, CancellationToken cancellationToken)
    {
        var directives = context.Directives;
        if (!directives.IsSubFlowResume ||
            directives.SubFlowResumeInstanceId is not { } subInstanceId ||
            directives.SubFlowResumeTerminalOutcome is not { } outcome)
        {
            return;
        }

        try
        {
            await correlationRepository.TryMarkSettledAsync(
                subInstanceId,
                outcome,
                directives.SubFlowResumeSettledAt ?? DateTime.UtcNow,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.SubItemSettlementMarkerWriteFailed(ex, outcome.ToString(), subInstanceId);
        }
    }
}
