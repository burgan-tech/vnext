using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Applies the common resting-state mutations used both inside the transition pipeline and after
/// a post-commit handoff has reloaded authoritative parent state.
/// </summary>
internal static class TransitionSettlement
{
    public static async Task ApplyAsync(
        TransitionExecutionContext context,
        InstanceStatus? resolvedStatus,
        bool endChainRequested,
        bool scheduleNotification,
        IInstanceRepository instanceRepository,
        IStateNotificationScheduler stateNotificationScheduler,
        ILogger logger,
        CancellationToken cancellationToken,
        bool guardChainOwnership = false)
    {
        // Busy-as-mutex: the chain runs with no held lease, so before settling verify that this
        // chain still owns the instance — a takeover (cancel/exit/timeout) or the reaper rotates
        // the durable token, and settling on top of the new owner would corrupt its status.
        // (The commit happens with the enclosing UoW; this guard closes the practical window —
        // the takeover side serializes its own flip under the short status lock.)
        if (guardChainOwnership && context.ChainToken.HasValue)
        {
            var snapshot = await instanceRepository.GetExecutionSnapshotAsync(
                context.InstanceId.ToString(), cancellationToken);

            if (snapshot is null || !snapshot.MatchesChain(context.ChainToken.Value))
            {
                logger.ChainOwnershipLost(context.InstanceId, context.ChainToken, snapshot?.ChainToken);
                return;
            }
        }

        var updated = false;
        if (context.Instance.IsBusy &&
            resolvedStatus is not null &&
            context.Target?.SubType != StateSubType.Busy &&
            !context.Instance.ActiveCorrelations.Any(c =>
                c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted))
        {
            context.Instance.Active();
            updated = true;
            logger.LogDebug(
                "Instance {InstanceId} resolved to Active after chain settlement",
                context.InstanceId);
        }

        if (!context.Instance.IsCompleted &&
            endChainRequested &&
            context.Instance.ChainToken.HasValue)
        {
            context.Instance.EndChain();
            updated = true;
            logger.LogDebug(
                "Instance {InstanceId} released chain ownership at rest",
                context.InstanceId);
        }

        if (updated)
            await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);

        if (scheduleNotification && context.Target?.HasStateNotifications == true)
        {
            await stateNotificationScheduler.ScheduleAsync(context, cancellationToken);
            logger.LogDebug(
                "State notification scheduled for instance {InstanceId} in state {StateKey}",
                context.InstanceId,
                context.Target.Key);
        }
    }
}
