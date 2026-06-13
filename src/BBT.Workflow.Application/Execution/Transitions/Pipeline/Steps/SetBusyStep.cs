using System.Diagnostics;
using BBT.Aether.Aspects;
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
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(SetBusyStep)}");

        // Skip if instance is already Busy (chained auto transitions). Adopt the instance's
        // existing chain token so continuations from this hop carry it forward.
        if (context.Instance.IsBusy)
        {
            context.ChainToken ??= context.Instance.ChainToken;
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

        // Begin (or adopt) the auto-chain ownership token, mark Busy, and persist.
        // A fresh request mints a new token; a continuation carries one in via context.ChainToken.
        var chainToken = context.ChainToken ?? Guid.NewGuid();
        context.ChainToken = chainToken;

        return await Result.Ok(context)
            .Tap(ctx => ctx.Instance.BeginChain(chainToken))
            .TapAsync(ctx => instanceRepository.UpdateAsync(ctx.Instance, true, cancellationToken))
            .Tap(ctx => logger.LogDebug(
                "Instance {InstanceId} set to Busy (chain {ChainToken}) for transition {TransitionKey}",
                ctx.InstanceId,
                chainToken,
                ctx.TransitionKey))
            .Map(_ => StepOutcome.Continue());
    }
}
