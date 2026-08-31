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
        // The resting-status flip closes a transition: a status write, its lock, and the state
        // notification. It ran unnamed at the very end of the pipeline, so a trace showed the last
        // step finishing and then a stretch of nothing before the hop ended.
        using var activity = PipelineStepActivityHelper.StartOperationActivity("Transition.Settle");
        activity?.SetTag(TelemetryConstants.TagNames.SettledStatus, resolvedStatus?.Code ?? "none");

        if (context.OwnsStatus &&
            context.Instance.IsBusy &&
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

            // One set-based CAS instead of the tracked full-row save. resolvedStatus only ever
            // carries Active (ResolveAvailableStep / ClearBusyOnResumeStep) and the old write was
            // Active() unconditionally, so Busy → Active CAS is behavior-identical; a lost CAS
            // means the row is no longer Busy and the flip is moot. Pending tracked changes still
            // commit with the enclosing unit of work.
            if (await instanceRepository.TryReleaseBusyAsync(context.Instance, cancellationToken))
            {
                logger.LogDebug(
                    "Instance {InstanceId} resolved to Active after chain settlement",
                    context.InstanceId);
            }
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
