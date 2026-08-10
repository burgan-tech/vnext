using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Applies the common resting-state mutations used both inside the transition pipeline and after
/// a post-commit handoff has reloaded authoritative parent state.
/// </summary>
internal static class TransitionSettlement
{
    /// <summary>
    /// Applies the resolved resting status. <c>statusLock</c> serializes the Busy→Active flip
    /// with the other status writers (reserve, takeover, fault); pass null when the caller
    /// ALREADY holds the status lock for this key (post-commit settlement) — a second acquire
    /// would fail, not reenter.
    /// </summary>
    public static async Task ApplyAsync(
        TransitionExecutionContext context,
        InstanceStatus? resolvedStatus,
        bool scheduleNotification,
        IInstanceRepository instanceRepository,
        IStateNotificationScheduler stateNotificationScheduler,
        ILogger logger,
        CancellationToken cancellationToken,
        IInstanceStatusLock? statusLock = null)
    {
        if (context.Instance.IsBusy &&
            resolvedStatus is not null &&
            context.Target?.SubType != StateSubType.Busy &&
            !context.Instance.ActiveCorrelations.Any(c =>
                c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted))
        {
            // Serialize the flip with reserves/takeovers. On acquisition failure proceed
            // unguarded — leaving the chain's own settlement unapplied would strand the
            // instance Busy. The write itself commits with the enclosing UoW; the lock
            // serializes the flip moment, not the commit (documented, accepted window).
            ITransitionLockScope? scope = null;
            if (statusLock is not null)
                scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);

            await using var _ = scope;

            context.Instance.Active();
            logger.LogDebug(
                "Instance {InstanceId} resolved to Active after chain settlement",
                context.InstanceId);

            await instanceRepository.UpdateAsync(context.Instance, true, cancellationToken);
        }

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
