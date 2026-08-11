using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Admission;

/// <inheritdoc />
/// <remarks>
/// Ordering invariant: every status flip performed here commits inside the busy manager's
/// RequiresNew UoW while the short status lock is still held — the flip is durable before the
/// lock releases, so a competing admission can never observe a stale status.
/// </remarks>
public sealed class TransitionAdmissionService(
    IInstanceStatusLock statusLock,
    IInstanceBusyManager busyManager,
    ILogger<TransitionAdmissionService> logger) : ITransitionAdmissionService
{
    /// <inheritdoc />
    public AdmissionKind Classify(TransitionExecutionContext context)
    {
        // Well-known kinds resolve from the definition + key — shared with ClassifyKey so the
        // app-service fast-fail and the pipeline prologue can never disagree.
        if (context.Transition?.Key is { } key)
        {
            var wellKnown = ClassifyKey(context.Workflow, key);
            if (wellKnown != AdmissionKind.Normal)
                return wellKnown;
        }

        if (context.Directives.IsTimeoutTransition)
            return AdmissionKind.BypassBusyCheck;

        // Subflow resume / long-poll ack resume own the Busy instance by directive; a
        // background-job re-entry (async accept or chain continuation) owns it because the
        // accept already reserved it. Shared transitions and state transitions classify as
        // Normal — they are subject to the Busy 409.
        if (context.Directives.IsInternalResume || context.IsPreReserved)
            return AdmissionKind.OwnerReentry;

        return AdmissionKind.Normal;
    }

    /// <inheritdoc />
    public AdmissionKind ClassifyKey(Definitions.Workflow workflow, string transitionKey)
    {
        if (Matches(transitionKey, WellKnownTransitionKeys.UpdateData, workflow.UpdateData?.Key))
            return AdmissionKind.Unconditional;

        if (Matches(transitionKey, WellKnownTransitionKeys.Cancel, workflow.Cancel?.Key)
            || Matches(transitionKey, WellKnownTransitionKeys.Exit, workflow.Exit?.Key))
            return AdmissionKind.BypassBusyCheck;

        return AdmissionKind.Normal;

        // Same matching rules as the TransitionExecutionContext extensions: the reserved alias
        // (accepted on the request side) or the workflow's configured key, OrdinalIgnoreCase.
        static bool Matches(string requested, string alias, string? configuredKey)
            => requested.Equals(alias, StringComparison.OrdinalIgnoreCase)
               || (configuredKey is not null
                   && configuredKey.Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public bool IsSubflowForward(TransitionExecutionContext context)
        => Classify(context) == AdmissionKind.Normal
           && context.Instance.IsBusy
           && context.Instance.HasActiveSubFlow;

    /// <inheritdoc />
    public Result CheckAdmission(TransitionExecutionContext context)
    {
        if (Classify(context) != AdmissionKind.Normal)
            return Result.Ok();

        if (context.Instance.IsBusy)
        {
            // A Busy parent with an open SubFlow is not a conflict: the request is admitted and
            // ForwardToActiveSubflowStep relays it to the subflow, which runs its own admission.
            if (context.Instance.HasActiveSubFlow)
                return Result.Ok();

            logger.TransitionRejectedInstanceBusy(context.InstanceId, context.TransitionKey);
            return Result.Fail(WorkflowErrors.InstanceBusy(context.InstanceId, context.TransitionKey));
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task<Result> ReserveAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return Result.Fail(WorkflowErrors.InstanceLockConflict(context.InstanceId));

        var outcome = await busyManager.TryMarkBusyWithPropagationAsync(
            context.InstanceId, cancellationToken);

        switch (outcome)
        {
            case BusyMarkOutcome.Marked:
                logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
                return Result.Ok();

            case BusyMarkOutcome.AlreadyBusy:
                logger.TransitionRejectedInstanceBusy(context.InstanceId, context.TransitionKey);
                return Result.Fail(WorkflowErrors.InstanceBusy(context.InstanceId, context.TransitionKey));

            default: // Skipped — completed or vanished between context creation and reserve
                return Result.Fail(ExecutionErrors.InstanceAlreadyCompleted(
                    context.InstanceId, context.Instance.Status.Description));
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryReserveOpportunisticallyAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return false; // degrade to data-only — an updateData accept is never rejected here

        var outcome = await busyManager.TryMarkBusyWithPropagationAsync(
            context.InstanceId, cancellationToken);

        if (outcome != BusyMarkOutcome.Marked)
            return false;

        logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
        return true;
    }

    /// <inheritdoc />
    public async Task<Result> TakeOverAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return Result.Fail(WorkflowErrors.InstanceLockConflict(context.InstanceId));

        // Unconditional flip: exempt from the Busy 409, but the flip itself is serialized under
        // the same short lock as every reserve/settle. Idempotent when already Busy; a Completed
        // instance is left untouched — HandleCancelPreflightStep surfaces the terminal error.
        await busyManager.MarkBusyAsync(context.InstanceId, cancellationToken);
        logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task ReleaseReservationAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
            if (!scope.IsAcquired)
            {
                logger.StatusLockAcquireFailed(context.LockKey);
                return;
            }

            var released = await busyManager.TryReleaseAsync(context.InstanceId, cancellationToken);
            if (released)
                logger.InstanceStatusSettled(context.InstanceId, InstanceStatus.Active.Code);
        }
        catch (Exception exception)
        {
            // Compensation must never mask the original failure; a stranded Busy is
            // recovered by job-timeout recovery.
            logger.ReservationReleaseFailed(exception, context.InstanceId);
        }
    }
}
