using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Orchestrates the execution of transition lifecycle steps in a deterministic order.
/// Admission is Busy-as-mutex: the first request-handling hop takes the short status lock
/// only for the Active→Busy check-and-set; the pipeline body and its auto-chain then run
/// with no held lease — the Busy flag itself is the mutual exclusion.
/// </summary>
public class TransitionPipeline
{
    private readonly TransitionExecutor _executor;
    private readonly ContinuationDispatcher _continuationDispatcher;
    private readonly IInstanceBusyManager _busyMarker;
    private readonly ITransitionContextFactory _contextFactory;
    private readonly IInstanceRepository _instanceRepository;
    private readonly IInstanceJobRepository _instanceJobRepository;
    private readonly IUnitOfWorkManager _uowManager;
    private readonly ITransitionValidationService _validationService;
    private readonly IPipelineProfileResolver _profileResolver;
    private readonly IStateNotificationScheduler _stateNotificationScheduler;
    private readonly ITransitionAdmissionService _admissionService;
    private readonly IInstanceStatusLock _statusLock;
    private readonly ILogger<TransitionPipeline> _logger;

    /// <summary>
    /// Maximum allowed chain depth for automatic transitions.
    /// Prevents infinite loops in recursive transition chains.
    /// </summary>
    private const int MaxChainDepth = 50;

    /// <summary>
    /// Pipeline error codes that represent a caller-actionable condition rather than an internal
    /// fault. When a step fails with one of these, the instance is still faulted but the failure is
    /// propagated to the caller so it maps to the intended HTTP status (e.g. 409) instead of being
    /// swallowed into the default "200 + Status=F" outcome. Extend deliberately — each entry must
    /// have a matching non-500 mapping in <c>AddExceptionHandling</c>.
    /// </summary>
    private static readonly HashSet<string> ClientFacingErrorCodes = new(StringComparer.Ordinal)
    {
        WorkflowErrorCodes.ResourceLockConflict,
        WorkflowErrorCodes.InstanceBusy,
    };

    private static bool IsClientFacingError(Error error)
        => error.Code is not null && ClientFacingErrorCodes.Contains(error.Code);

    /// <summary>
    /// Initializes a new instance of the TransitionPipeline.
    /// </summary>
    public TransitionPipeline(
        TransitionExecutor executor,
        ContinuationDispatcher continuationDispatcher,
        IInstanceBusyManager busyMarker,
        ITransitionContextFactory contextFactory,
        IInstanceRepository instanceRepository,
        IInstanceJobRepository instanceJobRepository,
        IUnitOfWorkManager uowManager,
        ITransitionValidationService validationService,
        IPipelineProfileResolver profileResolver,
        IStateNotificationScheduler stateNotificationScheduler,
        ITransitionAdmissionService admissionService,
        IInstanceStatusLock statusLock,
        ILogger<TransitionPipeline> logger)
    {
        _executor = executor;
        _continuationDispatcher = continuationDispatcher;
        _busyMarker = busyMarker;
        _contextFactory = contextFactory;
        _instanceRepository = instanceRepository;
        _instanceJobRepository = instanceJobRepository;
        _uowManager = uowManager;
        _validationService = validationService;
        _profileResolver = profileResolver;
        _stateNotificationScheduler = stateNotificationScheduler;
        _admissionService = admissionService;
        _statusLock = statusLock;
        _logger = logger;
    }

    /// <summary>
    /// Executes the transition pipeline: context creation → cheap Busy pre-check → policy
    /// validation → per-kind admission (Normal reserves under the short status lock; cancel/
    /// exit/timeout bypass the Busy check; updateData runs unconditionally; pre-reserved job
    /// re-entries and internal resumes skip the reserve). The chain then runs with no lock —
    /// only the very first request-handling hop is gated.
    /// </summary>
    public async Task<Result<TransitionExecutionContext>> RunAsync(
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        // 1) Create the context WITHOUT validation — the Busy pre-check runs first so a
        //    Busy instance is rejected before any validation work.
        var contextResult = await _contextFactory.CreateAsync(workflowContext, cancellationToken);
        if (!contextResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(contextResult.Error);

        var context = contextResult.Value!;
        context.Profile = _profileResolver.Resolve(workflowContext, context);
        context.EnqueueContinuations = workflowContext.EnqueueContinuations;
        context.IsPreReserved = workflowContext.IsPreReserved;
        context.SubflowChainReserved = workflowContext.SubflowChainReserved;

        // 2) Cheap Busy pre-check (aggregate already loaded — no extra round trip).
        var admission = _admissionService.CheckAdmission(context);
        if (!admission.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(admission.Error);

        // 3) Validation. Policy always: state-machine specifications read the CURRENT state, which
        //    changes between hops. Schema only for a request whose payload nobody has validated yet
        //    — this is the sync path's single validation point, the async path having validated at
        //    its accept. A job re-entry (IsPreReserved) carries a payload the accept already
        //    validated, and the start path validates before it persists the instance row and says
        //    so with PayloadSchemaValidated; both skip it rather than re-read the schema component
        //    and re-run the validator over identical bytes.
        var payloadAlreadyValidated = workflowContext.PayloadSchemaValidated || context.IsPreReserved;
        var validationResult = payloadAlreadyValidated
            ? await _validationService.ValidatePolicyAsync(context, cancellationToken)
            : await _validationService.ValidateAsync(context, cancellationToken);
        if (!validationResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(validationResult.Error);

        if (context.SkipImmediateExecution)
            return Result<TransitionExecutionContext>.Ok(context);

        // 4) Admission by kind — the only place a distributed lock is taken, and only for
        //    the milliseconds of the status check-and-set.
        switch (_admissionService.Classify(context))
        {
            case AdmissionKind.Unconditional:
            {
                // updateData: always accepted, and NEVER a status owner — it neither reserves
                // Busy nor settles it (ResolveAvailable/settlement are owner-gated, SetBusyStep
                // is exempt). The pipeline still runs in full: the data is written, tasks run
                // and auto transitions are evaluated against the fresh data. When an auto
                // transition is satisfied, the continuation boundary in RunChainAsync performs
                // a REAL reserve and the chained transition proceeds as a proper owner. A
                // parent with an open SubFlow correlation short-circuits to data-only in
                // HandleUpdateDataDataOnlyStep.
                context.OwnsStatus = false;
                return await RunChainAsync(context, cancellationToken);
            }

            case AdmissionKind.BypassBusyCheck:
            {
                // cancel / exit / timeout: exempt from the Busy 409, but the status flip still
                // goes through the same short distributed lock — admission marks Busy under it.
                //
                // Unless the accept already did it: an async accept flips Busy under its own
                // status lock before answering the caller, and the job re-enters pre-reserved.
                // Taking the lock again here would only re-acquire it to re-confirm a flag we
                // already own, putting this path back on two locks per request.
                if (!context.IsPreReserved)
                {
                    var takeover = await _admissionService.TakeOverAsync(context, cancellationToken);
                    if (!takeover.IsSuccess)
                        return Result<TransitionExecutionContext>.Fail(takeover.Error);
                }

                context.OwnsStatus = true;
                return await RunChainAsync(context, cancellationToken);
            }

            case AdmissionKind.OwnerReentry:
                // SubFlow resume resumes an already-Busy instance; confirm the busy mark.
                // (Long-poll acknowledge resume is intentionally NOT re-marked: a redundant
                // resume that no-ops must not strand an already-advanced instance in Busy.)
                if (context.Directives.IsSubFlowResume)
                    await _busyMarker.MarkBusyAsync(context.InstanceId, cancellationToken);

                context.OwnsStatus = true;
                return await RunChainAsync(context, cancellationToken);

            default: // AdmissionKind.Normal
            {
                // A Busy parent with an active SubFlow is admitted WITHOUT a reserve: the
                // instance stays Busy for the subflow's lifetime by design, and
                // ForwardToActiveSubflowStep (order 10) relays the request to the subflow,
                // which runs the same admission logic in its own context. It does NOT own the
                // parent's status.
                //
                // Deliberately NO chain reserve here, unlike the async accept: a sync caller
                // blocks until the relay has actually reached the leaf, so there is no window in
                // which it could observe a stale Active. Reserving the chain here would only
                // widen the stranded-Busy surface.
                if (_admissionService.IsSubflowForward(context))
                    return await RunChainAsync(context, cancellationToken);

                var reserve = await _admissionService.ReserveAsync(context, cancellationToken);
                if (!reserve.IsSuccess)
                    return Result<TransitionExecutionContext>.Fail(reserve.Error);

                context.OwnsStatus = true;
                return await RunChainAsync(context, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Runs the full transition chain (first + auto-chained) with no held lock: admission has
    /// already reserved the instance (or the request kind is exempt), so the Busy flag carries
    /// mutual exclusion across every hop with zero per-hop overhead.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> RunChainAsync(
        TransitionExecutionContext initialContext,
        CancellationToken cancellationToken)
    {
        var context = initialContext;
        var hop = 0;

        while (true)
        {
            hop++;

            // The transaction span already names the FIRST transition (the job span is
            // "TransitionJob.Execute/{key}"; on the sync path the route carries the key), so hop 1
            // needs no span of its own — that redundancy is exactly what the old `transition/{key}`
            // node was. A chained hop is a DIFFERENT transition running under the same transaction,
            // so it gets a group span; without one, two transitions' step spans would sit side by
            // side under one parent with nothing to tell them apart. It also gives
            // TransitionExecutor.EnrichTelemetry a per-hop span to tag, instead of every hop
            // overwriting the transaction's tags.
            using var hopActivity = hop == 1
                ? null
                : PipelineStepActivityHelper.StartOperationActivity($"Transition.{context.TransitionKey}");

            // Guard: Prevent infinite chain loops
            if (context.ChainDepth > MaxChainDepth)
            {
                _logger.TransitionChainDepthExceeded(context.ChainDepth, MaxChainDepth, context.TransitionKey);
                return Result<TransitionExecutionContext>.Fail(
                    WorkflowErrors.TransitionChainDepthExceeded(
                        context.ChainDepth, MaxChainDepth, context.TransitionKey));
            }

            // Execute pipeline steps for this transition
            var pipelineResult = await _executor.ExecuteOneAsync(context, cancellationToken);
            if (!pipelineResult.IsSuccess)
            {
                await MarkInstanceFaultedAsync(context, pipelineResult.Error, cancellationToken);

                // The instance is now faulted (F) regardless. For caller-actionable errors
                // (e.g. ResourceLockConflict) propagate the failure so the HTTP layer returns the
                // mapped status (409) instead of swallowing it into "200 + Status=F". All other
                // pipeline faults keep the existing 200 + Status=F behavior.
                if (IsClientFacingError(pipelineResult.Error))
                    return Result<TransitionExecutionContext>.Fail(pipelineResult.Error);

                return Result<TransitionExecutionContext>.Ok(context);
            }

            // updateData continuation handoff: updateData never owns the status, so a satisfied
            // auto transition must not advance under a non-owner. Reserve HERE (short status
            // lock) before any dispatch — inline hops inherit the ownership, enqueued hops
            // re-enter as genuinely pre-reserved.
            //
            // When the reserve fails the Busy has one of two meanings, and they must be told
            // apart. A state with automatic transitions PARKS Busy at rest (ResolveAvailableStep
            // deliberately never resolves it) — every fan-in wait state is such a state, its
            // Busy has NO owner, and dropping there would stall the gate forever. A Busy with a
            // LIVE owner shows up as an active transition job for a different transition key
            // (async accept intents and per-hop chain jobs; armed timers count as owners too —
            // they will fire and re-evaluate). Only that case is dropped: the owner is already
            // advancing, and every later updateData re-evaluates against fresher data. Parked
            // Busy is taken over instead (idempotent flip under the same short lock). An
            // in-process sync chain leaves no job row and is invisible to this probe, and there is
            // NO duplicate transition-record guard downstream to catch the loser — only the
            // per-hop policy checks do. Duplicate REQUESTS are stopped one layer up, by the
            // accept-time active-job guard inside the admission lock.
            var reservedForHandoff = false;
            if (context.Directives.NextTransition is { } handoff
                && !context.OwnsStatus
                && context.IsUpdateDataTransition())
            {
                var reserve = await _admissionService.ReserveAsync(context, cancellationToken);
                var handedOff = reserve.IsSuccess;

                if (!handedOff && !await HasLiveTransitionOwnerAsync(context, cancellationToken))
                {
                    var takeover = await _admissionService.TakeOverAsync(context, cancellationToken);
                    handedOff = takeover.IsSuccess;
                }

                if (handedOff)
                {
                    context.OwnsStatus = true;
                    reservedForHandoff = true;
                    _logger.UpdateDataContinuationReserved(
                        context.InstanceId, context.TransitionKey, handoff.TransitionKey);
                }
                else
                {
                    context.Directives.ConsumeNextTransition();
                    _logger.UpdateDataContinuationDropped(
                        context.InstanceId, context.TransitionKey, handoff.TransitionKey, reserve.Error.Code);
                }
            }

            // A post-commit job marks the handoff boundary. The runner owns executing this
            // remote work after the originating UoW has committed.
            // Do not consume the jobs: it returns the intact directives to the runner.
            if (context.Directives.PostCommitJobs.Count > 0)
            {
                // Keep the continuation intact so the runner awaits it after commit and child handoff.
                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Auto-chain always runs in the awaited pipeline loop, including async entry jobs.
            // Capture before dispatch because the strategy consumes NextTransition.
            var hadNextTransition = context.Directives.NextTransition is not null;

            var continuationResult = await _continuationDispatcher.DispatchAsync(
                ContinuationMode.Inline, context, cancellationToken);
            if (!continuationResult.IsSuccess)
            {
                // Same compensation as the barrier path: a handoff reserve without a live
                // continuation would leave the instance Busy with no owner.
                if (reservedForHandoff)
                    await _admissionService.ReleaseReservationAsync(context, cancellationToken);

                return Result<TransitionExecutionContext>.Fail(continuationResult.Error);
            }

            if (continuationResult.Value is null)
            {
                // The inline chain is complete; apply the deferred status.
                await TransitionSettlement.ApplyAsync(
                    context,
                    context.Directives.ConsumeResolvedStatus(),
                    scheduleNotification: !hadNextTransition,
                    _instanceRepository,
                    _stateNotificationScheduler,
                    _logger,
                    cancellationToken,
                    chainSettled: !hadNextTransition,
                    statusLock: _statusLock);

                return Result<TransitionExecutionContext>.Ok(context);
            }

            // Rebuild and validate the next chained transition context (single source of truth).
            var nextContextResult = await CreateAndValidateContextAsync(continuationResult.Value, context, cancellationToken);
            if (!nextContextResult.IsSuccess)
                return Result<TransitionExecutionContext>.Fail(nextContextResult.Error);

            context = nextContextResult.Value!;
        }
    }

    /// <summary>
    /// Whether the instance's Busy has a LIVE owner: an active transition job (async accept
    /// intent, per-hop chain job, or an armed/firing timer) targeting a DIFFERENT transition
    /// than this execution's own. Rows for this execution's own transition key are its own
    /// accept intent (or a concurrent duplicate of it) — neither is a chain owner. Used by the
    /// updateData continuation handoff to tell owned Busy (drop and let the owner advance)
    /// from parked Busy at an auto-gated rest state (take over, or the gate stalls forever).
    /// </summary>
    private async Task<bool> HasLiveTransitionOwnerAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activeJobs = await _instanceJobRepository.GetListActiveAsync(
            context.InstanceId, cancellationToken);

        return activeJobs.Any(job =>
            job.JobType is JobType.AsyncTransition or JobType.ScheduledTransition
            && !string.Equals(job.TransitionKey, context.TransitionKey, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates and validates a transition context from a workflow context.
    /// Used by auto-chain iterations (the initial entry runs the admission prologue instead).
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> CreateAndValidateContextAsync(
        WorkflowExecutionContext workflowContext,
        TransitionExecutionContext previous,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Only this in-loop path reuses tracked entities. Runner/post-commit/retry entries still
        // call CreateAsync in their new scope and rehydrate authoritative state.
        // Preserve GetActiveAsync's terminal guard without issuing another aggregate query.
        if (previous.Instance.IsCompleted)
            return Result<TransitionExecutionContext>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceCompleted,
                $"Instance {previous.InstanceId} is already completed with status: {previous.Instance.Status.Code}",
                workflowContext.InstanceId));

        var contextResult = _contextFactory.CreateFromPreloaded(
            workflowContext, previous.Workflow, previous.Instance);
        if (!contextResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(contextResult.Error);

        var context = contextResult.Value!;

        // Policy-only: state-machine specifications re-run on every hop because the current
        // state changes between auto-chain iterations. Schema validation, by contrast, runs
        // once at the request intake (HTTP app service, async accept, event delivery, start)
        // — every pipeline entry is either such an intake's dispatch or a system-generated
        // hop whose payload was produced by the engine itself.
        var validationResult = await _validationService.ValidatePolicyAsync(context, cancellationToken);
        if (!validationResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(validationResult.Error);

        context.Profile = _profileResolver.Resolve(workflowContext, context);
        context.EnqueueContinuations = workflowContext.EnqueueContinuations;
        context.IsPreReserved = workflowContext.IsPreReserved;
        context.SubflowChainReserved = workflowContext.SubflowChainReserved;
        context.OwnsStatus = workflowContext.OwnsStatus;
        return Result<TransitionExecutionContext>.Ok(context);
    }

    /// <summary>
    /// Marks the workflow instance as faulted. The fault flip is serialized under the short
    /// status lock (best effort — on acquisition failure it proceeds unguarded, because leaving
    /// a failed pipeline invisible is worse than a narrow double-write window).
    /// Uses a RequiresNew UoW scope so that any dirty state left on the current
    /// DbContext by the failed pipeline step does not block SaveChanges.
    /// </summary>
    private async Task MarkInstanceFaultedAsync(
        TransitionExecutionContext context,
        Error error,
        CancellationToken cancellationToken)
    {
        _logger.InstanceFaultedDueToPipelineError(context.InstanceId, error.Code, error.Message);

        // The failure path's own rest point: a status lock, a fresh unit of work, a reload and a
        // commit that used to run unnamed after the failing step's span had already closed.
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "Instance.Fault", context.TransitionKey);
        activity?.SetTag("error.code", error.Code);
        activity?.SetStatus(ActivityStatusCode.Error, error.Message);

        await using var statusScope = await _statusLock.AcquireAsync(context.LockKey, cancellationToken);

        await using var faultUow = _uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        // Reload in the new scope so we operate on a clean, tracked entity. Narrow shape on
        // purpose: Fault() walks the correlations for the child-fault cascade and puts LatestData
        // in the event payload — FindWithAllCorrelationsAndDataAsync covers both as split queries,
        // where FindAsync(id, includeDetails: true) produced one cartesian JOIN over
        // DataList × Correlations.
        var instance = await _instanceRepository.FindWithAllCorrelationsAndDataAsync(context.InstanceId, cancellationToken)
                       ?? context.Instance;

        if (!instance.HasActiveIncident)
        {
            var incident = InstanceIncidentFactory.Create(
                state: instance.GetCurrentState,
                transition: context.TransitionKey,
                taskKey: null,
                message: error.Message ?? "Unhandled pipeline error",
                errorCode: error.Code ?? "Pipeline:Unhandled",
                errorLayer: "Pipeline",
                traceId: context.TraceId);

            instance.AddIncident(incident);
        }

        instance.Fault(context.Domain, context.CallerMode == ExecMode.Sync);
        await _instanceRepository.UpdateAsync(instance, true, cancellationToken);
        ActivityContext commitContext;
        using (var commitActivity = PipelineStepActivityHelper.StartTransitionActivity(
                   "Uow.Commit", context.TransitionKey))
        {
            await faultUow.CommitAsync(cancellationToken);
            commitContext = commitActivity?.Context ?? default;
        }

        // Faulted is a rest point the client observes, so the activation episode closes here —
        // after the commit, like every other rest point. Regardless of OwnsStatus: whatever this
        // execution owned, the instance now rests Faulted because of it.
        ActivationActivity.Emit(
            PipelineStepActivityHelper.ActivitySource,
            TelemetryConstants.ActivationOutcomes.Faulted,
            context.InstanceId,
            context.Domain,
            context.WorkflowKey,
            context.TransitionKey,
            instance.GetCurrentState,
            settlingCommit: commitContext);

        _logger.InstanceFaultedSuccessfully(context.InstanceId);
    }
}
