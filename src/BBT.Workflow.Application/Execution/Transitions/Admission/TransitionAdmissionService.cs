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

        // A subflow error-boundary transition resumes the parent under the Busy ownership that
        // the blocking subflow established for its lifetime. Treating it as Normal would reject
        // the expected Busy parent after the correlation is completed (and therefore no longer
        // qualifies for subflow forwarding). Scope this exemption specifically to error-boundary
        // transitions: IsReentry also covers timers and retries, which must retain their own
        // admission semantics.
        if (context.IsErrorBoundaryTransition)
            return AdmissionKind.OwnerReentry;

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
    public async Task<Result> AcceptAsync(
        TransitionExecutionContext context,
        Func<AcceptFlip, CancellationToken, Task<Result>> underLock,
        CancellationToken cancellationToken = default)
    {
        // updateData accepts UNCONDITIONALLY and in PARALLEL — no lock, mirroring the sync path
        // (TransitionPipeline's Unconditional case never locked). The lock's only job here is to
        // serialize the status flip with the duplicate-job guard, and updateData has neither: it
        // is status-neutral (flip = None), and the guard does not apply to it — N simultaneous
        // updateData requests with the same logical identity are LEGITIMATE, each carrying its
        // own payload, and the enqueue is collision-free by construction (job id/name are unique
        // per request). Instance-data writes are serialized downstream by the per-instance write
        // funnel. Taking the lock anyway made N parallel notifiers fight over the parent's key
        // and pushed every loser into its error-boundary retry ladder for nothing.
        if (Classify(context) == AdmissionKind.Unconditional)
            return await underLock(AcceptFlip.None, cancellationToken);

        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return Result.Fail(WorkflowErrors.InstanceLockConflict(context.InstanceId));

        var flipResult = await FlipUnderLockAsync(context, cancellationToken);
        if (!flipResult.IsSuccess)
            return Result.Fail(flipResult.Error);

        var flip = flipResult.Value;

        Result outcome;
        try
        {
            outcome = await underLock(flip, cancellationToken);
        }
        catch
        {
            // A throwing enqueue strands the flip exactly as a failed one does, and for a chain
            // reserve it strands the LEAF — which no caller holds a handle to.
            await CompensateUnderLockAsync(context, flip, cancellationToken);
            throw;
        }

        if (!outcome.IsSuccess)
            await CompensateUnderLockAsync(context, flip, cancellationToken);

        return outcome;
    }

    /// <summary>
    /// Performs the status flip the request's kind calls for. Runs with the status lock already
    /// held, so it talks to the busy manager directly instead of going through
    /// <see cref="ReserveAsync"/> / <see cref="TakeOverAsync"/>, which would try to acquire the
    /// same non-reentrant key again.
    /// </summary>
    private async Task<Result<AcceptFlip>> FlipUnderLockAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        // Subflow resume / long-poll ack resume already own the Busy they will run under.
        if (context.Directives.IsInternalResume)
            return Result<AcceptFlip>.Ok(AcceptFlip.None);

        switch (Classify(context))
        {
            // updateData is status-neutral by design, and an owner re-entry already holds the flag.
            case AdmissionKind.Unconditional:
            case AdmissionKind.OwnerReentry:
                return Result<AcceptFlip>.Ok(AcceptFlip.None);

            case AdmissionKind.BypassBusyCheck:
            {
                // cancel / exit / timeout: exempt from the Busy 409, but they DO set the status,
                // so they take the same lock as everything else and flip under it. Idempotent —
                // only a flip we actually performed may be compensated.
                var flipped = await busyManager.MarkBusyAsync(context.InstanceId, cancellationToken);
                logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
                return Result<AcceptFlip>.Ok(flipped ? AcceptFlip.TakenOver : AcceptFlip.None);
            }

            default: // AdmissionKind.Normal
            {
                if (IsSubflowForward(context))
                {
                    // The parent is Busy for the subflow's lifetime by design and does not reserve
                    // itself; the chain BELOW it must be Busy before the caller is answered.
                    await busyManager.MarkBusyWithPropagationAsync(context.InstanceId, cancellationToken);
                    logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
                    return Result<AcceptFlip>.Ok(AcceptFlip.ChainReserved);
                }

                var admission = CheckAdmission(context);
                if (!admission.IsSuccess)
                    return Result<AcceptFlip>.Fail(admission.Error);

                var outcome = await busyManager.TryMarkBusyWithPropagationAsync(
                    context.InstanceId, cancellationToken);

                switch (outcome)
                {
                    case BusyMarkOutcome.Marked:
                        logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
                        return Result<AcceptFlip>.Ok(AcceptFlip.Reserved);

                    case BusyMarkOutcome.AlreadyBusy:
                        logger.TransitionRejectedInstanceBusy(context.InstanceId, context.TransitionKey);
                        return Result<AcceptFlip>.Fail(
                            WorkflowErrors.InstanceBusy(context.InstanceId, context.TransitionKey));

                    default: // Skipped — completed or vanished between context creation and accept
                        return Result<AcceptFlip>.Fail(ExecutionErrors.InstanceAlreadyCompleted(
                            context.InstanceId, context.Instance.Status.Description));
                }
            }
        }
    }

    /// <summary>
    /// Undoes an accept's own flip when the work under the lock failed. Talks to the busy manager
    /// directly — the Release* methods acquire the status lock this call still holds.
    /// Never throws: compensation must not mask the original failure.
    /// </summary>
    private async Task CompensateUnderLockAsync(
        TransitionExecutionContext context,
        AcceptFlip flip,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (flip)
            {
                case AcceptFlip.Reserved:
                case AcceptFlip.TakenOver:
                    if (await busyManager.TryReleaseAsync(context.InstanceId, cancellationToken))
                        logger.InstanceStatusSettled(context.InstanceId, InstanceStatus.Active.Code);
                    break;

                case AcceptFlip.ChainReserved:
                    await busyManager.ReleaseWithPropagationAsync(context.InstanceId, cancellationToken);
                    break;
            }
        }
        catch (Exception exception)
        {
            // A stranded Busy is recovered by job-timeout recovery.
            logger.ReservationReleaseFailed(exception, context.InstanceId);
        }
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
    public async Task<Result> ReserveSubflowChainAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = await statusLock.AcquireAsync(context.LockKey, cancellationToken);
        if (!scope.IsAcquired)
            return Result.Fail(WorkflowErrors.InstanceLockConflict(context.InstanceId));

        // MarkBusyWithPropagation, NOT TryMarkBusyWithPropagation: the relay levels are already
        // Busy and the Try- variant deliberately short-circuits on AlreadyBusy (its 409 contract),
        // so it would never reach the leaf — which is the only level the client can observe.
        await busyManager.MarkBusyWithPropagationAsync(context.InstanceId, cancellationToken);

        logger.InstanceBusyReserved(context.InstanceId, context.TransitionKey);
        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task ReleaseSubflowChainAsync(
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

            await busyManager.ReleaseWithPropagationAsync(context.InstanceId, cancellationToken);
        }
        catch (Exception exception)
        {
            // Compensation must never mask the original failure; a stranded Busy is
            // recovered by job-timeout recovery.
            logger.ReservationReleaseFailed(exception, context.InstanceId);
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
