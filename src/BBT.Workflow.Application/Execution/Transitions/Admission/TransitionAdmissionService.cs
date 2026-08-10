using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Admission;

/// <inheritdoc />
/// <remarks>
/// Ordering invariant: every status mutation performed here commits inside the busy manager's
/// RequiresNew UoW while the short status lock is still held — the flip is durable before the
/// lock releases, so a competing reserve can never observe a stale status.
/// </remarks>
public sealed class TransitionAdmissionService(
    IInstanceStatusLock statusLock,
    IInstanceBusyManager busyManager,
    IInstanceRepository instanceRepository,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<TransitionAdmissionService> logger) : ITransitionAdmissionService
{
    private bool Enabled => executionOptions.Value.UseBusyAsMutex;

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
        // background-job re-entry or per-job continuation owns it via the chain token stamped
        // at accept time. Shared transitions and state transitions classify as Normal — they
        // share the single instance lock key and are subject to the Busy 409.
        if (context.Directives.IsInternalResume || context.ChainToken.HasValue)
            return AdmissionKind.OwnerReentry;

        return AdmissionKind.Normal;
    }

    /// <inheritdoc />
    public Result CheckAdmission(TransitionExecutionContext context)
    {
        if (!Enabled || Classify(context) != AdmissionKind.Normal)
            return Result.Ok();

        if (context.Instance.IsBusy)
        {
            logger.TransitionRejectedInstanceBusy(context.InstanceId, context.TransitionKey);
            return Result.Fail(WorkflowErrors.InstanceBusy(context.InstanceId, context.TransitionKey));
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task<Result<Guid>> ReserveAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var chainToken = context.ChainToken ?? Guid.NewGuid();

        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return Result<Guid>.Fail(WorkflowErrors.InstanceLockConflict(context.InstanceId));

        var outcome = await busyManager.TryReserveWithPropagationAsync(
            context.InstanceId, chainToken, cancellationToken);

        switch (outcome)
        {
            case BusyMarkOutcome.Marked:
                logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey, chainToken);
                return Result<Guid>.Ok(chainToken);

            case BusyMarkOutcome.AlreadyBusy:
                logger.TransitionRejectedInstanceBusy(context.InstanceId, context.TransitionKey);
                return Result<Guid>.Fail(WorkflowErrors.InstanceBusy(context.InstanceId, context.TransitionKey));

            default: // Skipped — completed or vanished between context creation and reserve
                return Result<Guid>.Fail(ExecutionErrors.InstanceAlreadyCompleted(
                    context.InstanceId, context.Instance.Status.Description));
        }
    }

    /// <inheritdoc />
    public async Task<Result<Guid>> TakeOverAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var chainToken = Guid.NewGuid();

        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return Result<Guid>.Fail(WorkflowErrors.InstanceLockConflict(context.InstanceId));

        var outcome = await busyManager.TakeOverAsync(context.InstanceId, chainToken, cancellationToken);

        if (outcome == BusyMarkOutcome.Skipped)
            return Result<Guid>.Fail(ExecutionErrors.InstanceAlreadyCompleted(
                context.InstanceId, context.Instance.Status.Description));

        logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey, chainToken);
        return Result<Guid>.Ok(chainToken);
    }

    /// <inheritdoc />
    public async Task<Result> VerifyOwnershipAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        // Directive-driven resumes (subflow resume, long-poll ack) may carry no token — their
        // ownership is asserted by the resume directive and the paths that issue it.
        if (!Enabled || !context.ChainToken.HasValue)
            return Result.Ok();

        var snapshot = await instanceRepository.GetExecutionSnapshotAsync(
            context.InstanceId.ToString(), cancellationToken);

        if (snapshot is null || !snapshot.MatchesChain(context.ChainToken.Value))
        {
            logger.ChainOwnershipLost(context.InstanceId, context.ChainToken, snapshot?.ChainToken);
            return Result.Fail(WorkflowErrors.ChainOwnershipLost(context.InstanceId));
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task ReleaseReservationAsync(
        TransitionExecutionContext context,
        Guid chainToken,
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

            var released = await busyManager.TryReleaseAsync(context.InstanceId, chainToken, cancellationToken);
            if (released)
                logger.InstanceStatusSettled(context.InstanceId, InstanceStatus.Active.Code);
        }
        catch (Exception exception)
        {
            // Compensation must never mask the original failure; a stranded Busy is
            // recovered by the chain reaper.
            logger.ReservationReleaseFailed(exception, context.InstanceId);
        }
    }
}
