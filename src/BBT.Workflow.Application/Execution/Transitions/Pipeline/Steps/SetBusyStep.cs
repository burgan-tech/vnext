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
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Skip if instance is already Busy (admission reserve or chained auto transitions).
        if (context.Instance.IsBusy)
        {
            logger.LogDebug(
                "Instance {InstanceId} is already Busy, skipping SetBusyStep",
                context.InstanceId);
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Skip if instance is completed (should not happen, but defensive)
        if (context.Instance.IsCompleted)
        {
            logger.LogDebug(
                "Instance {InstanceId} is already completed, skipping SetBusyStep",
                context.InstanceId);
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Skip for SubFlow resume - status is managed by ClearBusyOnResumeStep
        if (context.Directives.IsSubFlowResume)
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Mark Busy and persist (admission normally does this up front; this is the in-pipeline
        // safety net for profiles that reach here with an Active aggregate).
        return await Result.Ok(context)
            .Tap(ctx => ctx.Instance.Busy())
            .TapAsync(ctx => instanceRepository.UpdateAsync(ctx.Instance, true, cancellationToken))
            .Tap(ctx => logger.LogDebug(
                "Instance {InstanceId} set to Busy for transition {TransitionKey}",
                ctx.InstanceId,
                ctx.TransitionKey))
            .Map(_ => StepOutcome.Continue());
    }
}
