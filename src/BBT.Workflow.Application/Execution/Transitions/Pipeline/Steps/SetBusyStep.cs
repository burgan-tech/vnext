using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that sets the instance to Busy status at the start of transition processing.
/// This prevents concurrent modifications during transition processing.
/// Skips if instance is already Busy (for chained auto transitions).
/// </summary>
public sealed class SetBusyStep(
    IInstanceRepository instanceRepository,
    ILogger<SetBusyStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.SetBusy;

    /// <inheritdoc />
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // updateData never touches the instance status: it runs without ownership and must not
        // flip an Active instance to Busy (nothing would ever settle it back — a non-owner is
        // barred from ResolveAvailable/settlement). Ownership for a satisfied auto transition
        // is acquired at the continuation boundary instead.
        if (context.IsUpdateDataTransition())
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Skip if instance is already Busy (admission reserve or chained auto transitions).
        if (context.Instance.IsBusy)
        {
            logger.LogDebug(
                "Instance {InstanceId} is already Busy, skipping SetBusyStep",
                context.InstanceId);
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Skip if instance is completed (should not happen, but defensive)
        if (context.Instance.IsCompleted)
        {
            logger.LogDebug(
                "Instance {InstanceId} is already completed, skipping SetBusyStep",
                context.InstanceId);
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Skip for SubFlow resume - status is managed by ClearBusyOnResumeStep
        if (context.Directives.IsSubFlowResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.ContinueNoWork());
        }

        // Admission (Reserve/TakeOver) flipped the ROW under its status lock, in its own
        // RequiresNew DbContext — so this pipeline's aggregate can still read Active. Settlement's
        // owner guard reads context.Instance.IsBusy, so the in-memory aggregate MUST be aligned
        // here or the chain never settles back to Active (stranded Busy). No DB call: the row is
        // already Busy, and the status value simply rides along the next tracked flush.
        if (context.OwnsStatus)
        {
            context.Instance.Busy();
            logger.LogDebug(
                "Instance {InstanceId} aggregate aligned to admission's Busy for transition {TransitionKey}",
                context.InstanceId,
                context.TransitionKey);
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Safety net for paths that genuinely reach here with an Active row: one set-based CAS —
        // the tracked save used to rewrite the full row and flush every pending change
        // mid-pipeline; anything pending still commits with the enclosing unit of work.
        // A lost CAS means a concurrent writer changed the row after our guards — the old blind
        // write would have overwritten it; proceeding without the flip is strictly safer.
        var flipped = await instanceRepository.TryMarkBusyAsync(context.Instance, cancellationToken);
        if (flipped)
        {
            logger.LogDebug(
                "Instance {InstanceId} set to Busy for transition {TransitionKey}",
                context.InstanceId,
                context.TransitionKey);
        }
        else
        {
            logger.LogDebug(
                "Instance {InstanceId} was no longer Active at SetBusy for transition {TransitionKey}; continuing without the flip",
                context.InstanceId,
                context.TransitionKey);
        }

        return Result<StepOutcome>.Ok(StepOutcome.Continue());
    }
}
