using BBT.Aether.Results;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Admission;

/// <inheritdoc />
/// <remarks>
/// Ordering invariant: the reserve flip commits inside the busy manager's RequiresNew UoW while
/// the short status lock is still held — the flip is durable before the lock releases, so a
/// competing reserve can never observe a stale status.
/// </remarks>
public sealed class TransitionAdmissionService(
    IInstanceStatusLock statusLock,
    IInstanceBusyManager busyManager,
    ILogger<TransitionAdmissionService> logger) : ITransitionAdmissionService
{
    /// <inheritdoc />
    public AdmissionKind Classify(TransitionExecutionContext context)
    {
        if (context.IsUpdateDataTransition())
            return AdmissionKind.Unconditional;

        if (context.IsCancelTransition()
            || context.IsExitTransition()
            || context.Directives.IsTimeoutTransition)
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
    public Result CheckAdmission(TransitionExecutionContext context)
    {
        if (Classify(context) != AdmissionKind.Normal)
            return Result.Ok();

        if (context.Instance.IsBusy)
        {
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
