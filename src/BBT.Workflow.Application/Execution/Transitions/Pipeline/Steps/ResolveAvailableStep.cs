using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline.Steps;

/// <summary>
/// Pipeline step that resolves the Available (Active) status at the end of transition processing.
/// Sets instance to Active when all conditions are met:
/// - Auto transition chain is not continuing (NextTransition is null)
/// - Not a terminal state (SubFlow entry point for parent)
/// - Target state is not a Finish state
/// - Target state has only manual/event transitions or no transitions
/// </summary>
public sealed class ResolveAvailableStep(
    IInstanceRepository instanceRepository,
    ILogger<ResolveAvailableStep> logger) : ITransitionStep
{
    /// <inheritdoc />
    public int Order => LifecycleOrder.ResolveAvailable;

    /// <inheritdoc />
    [Trace]
    public async Task<Result<StepOutcome>> ExecuteAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        Activity.Current?.SetDisplayName($"[{Order}] {nameof(ResolveAvailableStep)}");

        // Check if instance should become Available
        if (!ShouldSetAvailable(context))
        {
            return Result<StepOutcome>.Ok(StepOutcome.Continue());
        }

        // Set instance to Active and persist
        return await Result.Ok(context)
            .Tap(ctx => ctx.Instance.Active())
            .TapAsync(ctx => instanceRepository.UpdateAsync(ctx.Instance, true, cancellationToken))
            .Tap(ctx => logger.LogDebug(
                "Instance {InstanceId} set to Available after transition to state {TargetState}",
                ctx.InstanceId,
                ctx.Target?.Key ?? "unknown"))
            .Map(_ => StepOutcome.Continue());
    }

    /// <summary>
    /// Determines if the instance should be set to Available (Active) status.
    /// </summary>
    private bool ShouldSetAvailable(TransitionExecutionContext context)
    {
        // Already completed or not busy - nothing to do
        if (context.Instance.IsCompleted || !context.Instance.IsBusy)
        {
            logger.LogDebug(
                "Instance {InstanceId} is not Busy or already completed, skipping ResolveAvailableStep",
                context.InstanceId);
            return false;
        }

        // Auto transition chain is continuing - stay Busy
        if (context.Directives.NextTransition != null)
        {
            logger.LogDebug(
                "Instance {InstanceId} has NextTransition ({NextTransition}), staying Busy",
                context.InstanceId,
                context.Directives.NextTransition.TransitionKey);
            return false;
        }

        // Terminal state reached (SubFlow entry point) - stay Busy
        if (context.Directives.TerminalReached)
        {
            logger.LogDebug(
                "Instance {InstanceId} reached terminal state, staying Busy",
                context.InstanceId);
            return false;
        }

        // Target is null - should not happen, but defensive
        if (context.Target == null)
        {
            logger.LogWarning(
                "Instance {InstanceId} has null target state, skipping ResolveAvailableStep",
                context.InstanceId);
            return false;
        }

        // Finish state - will be handled by HandleFinishStep (Completed status)
        if (context.Target.StateType == StateType.Finish)
        {
            logger.LogDebug(
                "Instance {InstanceId} is in Finish state, status will be Completed",
                context.InstanceId);
            return false;
        }

        // Target state has automatic or scheduled transitions - stay Busy
        // (This should already be caught by NextTransition check, but defensive)
        if (!context.Target.HasOnlyManualOrEventTransitions)
        {
            logger.LogDebug(
                "Instance {InstanceId} target state {TargetState} has auto/scheduled transitions, staying Busy",
                context.InstanceId,
                context.Target.Key);
            return false;
        }

        // All conditions met - set to Available
        logger.LogDebug(
            "Instance {InstanceId} meets all conditions for Available status in state {TargetState}",
            context.InstanceId,
            context.Target.Key);
        return true;
    }
}
