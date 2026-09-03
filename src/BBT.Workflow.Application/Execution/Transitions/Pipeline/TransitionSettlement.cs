using System.Diagnostics;
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
    /// <param name="chainSettled">
    /// True when no further hop continues this chain — nothing was enqueued and nothing runs
    /// in-process after this settlement. Only then can the activation episode have reached its rest
    /// point, and only then is an <see cref="ActivationVerdict"/> recorded on the directives.
    /// </param>
    public static async Task ApplyAsync(
        TransitionExecutionContext context,
        InstanceStatus? resolvedStatus,
        bool scheduleNotification,
        IInstanceRepository instanceRepository,
        IStateNotificationScheduler stateNotificationScheduler,
        ILogger logger,
        CancellationToken cancellationToken,
        bool chainSettled,
        IInstanceStatusLock? statusLock = null)
    {
        // The resting-status flip closes a transition: a status write, its lock, and the state
        // notification. It ran unnamed at the very end of the pipeline, so a trace showed the last
        // step finishing and then a stretch of nothing before the hop ended.
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "Transition.Settle", context.TransitionKey);
        activity?.SetTag(TelemetryConstants.TagNames.SettledStatus, resolvedStatus?.Code ?? "none");

        var hasOpenSubFlow = HasOpenSubFlow(context);
        var guardPassed =
            context.OwnsStatus &&
            context.Instance.IsBusy &&
            resolvedStatus is not null &&
            context.Target?.SubType != StateSubType.Busy &&
            !hasOpenSubFlow;
        var flipped = false;

        if (guardPassed)
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
            flipped = await instanceRepository.TryReleaseBusyAsync(context.Instance, cancellationToken);
            if (flipped)
            {
                logger.LogDebug(
                    "Instance {InstanceId} resolved to Active after chain settlement",
                    context.InstanceId);
            }
        }

        // What the CAS did is the one thing vnext.settle.status cannot say: the same value is
        // stamped whether this hop made the instance Active, lost the race, or never tried.
        activity?.SetTag(
            TelemetryConstants.TagNames.SettleCas,
            guardPassed ? (flipped ? "flipped" : "lost") : "skipped");

        if (flipped)
        {
            // The exact instant the instance became available, as an event on the settling span.
            // Pre-commit: TransitionRunner adds `instance.available.committed` on the transaction
            // once the write is durable.
            activity?.AddEvent(new ActivityEvent(
                "instance.available",
                tags: new ActivityTagsCollection
                {
                    { TelemetryConstants.TagNames.InstanceId, context.InstanceId.ToString() },
                    { TelemetryConstants.TagNames.StateTo, context.Target?.Key }
                }));
        }

        var verdict = ResolveVerdict(context, guardPassed, flipped, hasOpenSubFlow, chainSettled);
        if (verdict is not null)
            context.Directives.RecordActivation(verdict);
        activity?.SetTag(TelemetryConstants.TagNames.ActivationEmitted, verdict is not null);

        if (scheduleNotification && context.Target?.HasStateNotifications == true)
        {
            await stateNotificationScheduler.ScheduleAsync(context, cancellationToken);
            logger.LogDebug(
                "State notification scheduled for instance {InstanceId} in state {StateKey}",
                context.InstanceId,
                context.Target.Key);
        }
    }

    /// <summary>
    /// Decides whether this settlement closed the activation episode, and how. Null means the
    /// episode goes on (or was never this execution's to close): a hop that enqueued its
    /// continuation, a non-owning execution beside an in-flight chain, a CAS lost to a concurrent
    /// settler that emits its own verdict, or an instance that was already Active.
    /// </summary>
    private static ActivationVerdict? ResolveVerdict(
        TransitionExecutionContext context,
        bool guardPassed,
        bool flipped,
        bool hasOpenSubFlow,
        bool chainSettled)
    {
        if (!chainSettled || !context.OwnsStatus)
            return null;

        var instance = context.Instance;
        var stateTo = context.Target?.Key ?? instance.GetCurrentState;

        if (guardPassed)
        {
            // The row was no longer Busy: whoever flipped it closed the episode.
            return flipped
                ? new ActivationVerdict(TelemetryConstants.ActivationOutcomes.Active, CasFlipped: true, stateTo)
                : null;
        }

        if (instance.Status.Equals(InstanceStatus.Faulted))
            return new ActivationVerdict(TelemetryConstants.ActivationOutcomes.Faulted, CasFlipped: false, stateTo);

        if (instance.IsCompleted)
        {
            // Instance.Cancel and Instance.Complete both write Completed; the transition that got
            // here tells them apart (HandleFinishStep routes cancel/exit to Cancel()).
            var canceled = context.IsCancelTransition() || context.Target?.SubType == StateSubType.Cancelled;
            return new ActivationVerdict(
                canceled ? TelemetryConstants.ActivationOutcomes.Canceled : TelemetryConstants.ActivationOutcomes.Completed,
                CasFlipped: false,
                stateTo);
        }

        if (!instance.IsBusy)
        {
            // Already Active before this hop (a status-neutral owner such as a retry landing on a
            // resting instance): nothing became available here.
            return null;
        }

        // A live SubFlow is not a settlement for the parent: it is still Busy and must not emit a
        // misleading completed activation. The handoff carries the episode to the child, whose
        // activation span represents the next surface that actually becomes available.
        if (hasOpenSubFlow)
            return null;

        // Rests Busy, deliberately. Each of these is a state the client observes as "not yet".
        if (context.Target?.SubType == StateSubType.Busy)
            return new ActivationVerdict(TelemetryConstants.ActivationOutcomes.BusySubtype, CasFlipped: false, stateTo);

        return new ActivationVerdict(TelemetryConstants.ActivationOutcomes.BusyParked, CasFlipped: false, stateTo);
    }

    private static bool HasOpenSubFlow(TransitionExecutionContext context) =>
        context.Instance.ActiveCorrelations.Any(c =>
            c.SubFlowType.Equals(SubFlowType.SubFlow) && !c.IsCompleted);
}
