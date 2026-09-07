using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Strategies;

/// <summary>
/// Asynchronous transition execution strategy.
/// Executes transitions as background jobs for better scalability and fault tolerance.
/// <para>
/// The accept takes exactly ONE distributed lock, and it is the same short status lock the sync
/// pipeline uses: <see cref="ITransitionAdmissionService.AcceptAsync"/> acquires
/// <c>ctx.LockKey</c>, performs the kind's status flip (reserve / take-over / chain reserve /
/// nothing), and runs the duplicate-job guard and the durable enqueue while still holding it.
/// The guard shares that critical section because its check-then-insert has no database
/// constraint behind it — it is not a second concern deserving a second lock. Ordering is
/// fast-fail Busy check → validation → lock → flip → guard → enqueue → release, so no lock is
/// held during context creation or schema/policy validation. Callers see the instance Busy the
/// moment the request is accepted.
/// </para>
/// <para>
/// A Busy parent with an active SubFlow is admitted without a reserve of its own — it is Busy for
/// the subflow's lifetime by design — but its SubFlow chain IS reserved down to the leaf before the
/// response, because the state function reports the deepest active subflow's status. Skipping that
/// would answer the caller while the leaf still reads Active, and a client long polling on the
/// parent would see no work in progress. The relay carries the claim
/// (<c>TransitionInput.ChainReserved</c>) so the leaf's own admission treats the pre-set Busy as an
/// owner re-entry instead of a 409.
/// </para>
/// <para>
/// Enqueue atomicity is governed by <c>WorkflowExecutionOptions.DirectEnqueueContinuations</c>:
/// when ON (default), the durable intent + Dapr schedule commit in one unit of work (transactional
/// enqueue). On Dapr failure the gateway falls back to the transactional outbox. When OFF, the
/// outbox path is always used — the Inbox performs the Dapr enqueue (fully transactional, at the
/// cost of the outbox/inbox poll hop). Both paths use the same <see cref="ITransitionEnqueueGateway"/>.
/// </para>
/// </summary>
public sealed class AsyncTransitionStrategy(
    ITransitionContextFactory ctxFactory,
    IInstanceJobRepository jobRepository,
    ITransitionValidationService validationService,
    IUnitOfWorkManager uowManager,
    ITransitionAdmissionService admissionService,
    ITransitionEnqueueGateway enqueueGateway,
    ILogger<AsyncTransitionStrategy> logger) : ITransitionStrategy
{
    public ExecMode Mode => ExecMode.Async;

    /// <inheritdoc />
    /// <summary>
    /// Executes transition asynchronously by enqueuing a background job.
    /// Railway chain: Create Context → Validate (schema + policy) → Set Busy → Enqueue Job → Return Context
    /// </summary>
    /// <remarks>
    /// Validation must run BEFORE lock acquisition and job enqueue so that callers
    /// receive 400 Bad Request for invalid payloads instead of accepting the request,
    /// flipping the instance to Busy, and discovering the schema violation later in
    /// the background job (which would leave the instance in a Faulted state).
    /// This also guarantees correct behavior when callers bypass the AppService
    /// pre-validation guard and invoke the workflow execution service directly.
    /// </remarks>
    public Task<Result<TransitionExecutionContext>> ExecuteAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;
        return ctxFactory.CreateAsync(context, cancellationToken)
            .BindAsync(ctx => ValidateAsync(ctx, context.PayloadSchemaValidated, cancellationToken))
            .BindAsync(ctx => EnqueueJobAndReturnContextAsync(ctx, context, activity, cancellationToken));
    }

    /// <summary>
    /// Validates the transition context before any side effects (Busy flip, lock acquisition, job
    /// enqueue). Mirrors the guard in <c>TransitionPipeline.RunAsync</c> for the sync path.
    /// </summary>
    /// <remarks>
    /// Policy is re-validated always — it reads the instance's current state, which the caller may
    /// not have seen. The SCHEMA is skipped when the caller already validated the same payload
    /// against the same transition (the start path, which must validate before persisting the
    /// instance row); re-reading the schema component and re-running the validator over identical
    /// bytes cannot reach a different verdict.
    /// </remarks>
    private async Task<Result<TransitionExecutionContext>> ValidateAsync(
        TransitionExecutionContext ctx,
        bool payloadSchemaValidated,
        CancellationToken cancellationToken)
    {
        var validationResult = payloadSchemaValidated
            ? await validationService.ValidatePolicyAsync(ctx, cancellationToken)
            : await validationService.ValidateAsync(ctx, cancellationToken);
        return validationResult.IsSuccess
            ? Result<TransitionExecutionContext>.Ok(ctx)
            : Result<TransitionExecutionContext>.Fail(validationResult.Error);
    }

    /// <summary>
    /// Acquires a distributed lock on the instance before processing.
    /// Under the lock: checks for an existing active job (409 if found), then
    /// sets the instance to Busy and enqueues the background job.
    /// If the lock cannot be acquired, returns 409 — mirrors sync pipeline behavior.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> EnqueueJobAndReturnContextAsync(
        TransitionExecutionContext ctx,
        WorkflowExecutionContext context,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // One id, one name, built ONCE here: the job name is unique per enqueue (see JobName), so
        // rebuilding it downstream would produce a different string than the row and the scheduler
        // entry. The active-job guard below matches the LOGICAL identity instead (structured
        // columns), which is what "a job for this transition is already queued" actually means.
        var jobId = Guid.NewGuid();
        var sourceStateKey = ctx.Current?.Key ?? string.Empty;
        var jobName = JobName.ForAsyncTransition(
            ctx.InstanceId, sourceStateKey, context.TransitionKey, jobId);
        EnrichTelemetry(activity, ctx, jobName.Value);

        // Set inside the under-lock callback, read after it: the handle is non-null only when the
        // direct path recorded the job with arming deferred, i.e. when this method still owes the
        // scheduler a call.
        IBackgroundJobArmHandle? armHandle = null;

        // updateData (Unconditional) accepts every request, in parallel: no duplicate-job dedupe.
        // Two simultaneous updateData requests share the same logical identity (instance, source
        // state, transition key) yet are BOTH legitimate — each carries its own payload, and
        // dropping one would lose a caller's data. Physical collision is impossible anyway: the
        // job id/name above are unique per enqueue. Admission runs this kind's accept lock-free
        // for the same reason, so the guard would also be an unserialized check-then-insert here.
        var admissionKind = admissionService.Classify(ctx);

        // The accept's ONE distributed lock. Admission takes the status lock on ctx.LockKey,
        // performs the kind's status flip, and runs the callback below while still holding it —
        // the duplicate-job guard is a check-then-insert with no database constraint behind it,
        // so it has to share the critical section with the flip rather than get a lock of its own.
        // (Exception: Unconditional/updateData — no flip, no guard, no lock; see above.)
        var acceptResult = await admissionService.AcceptAsync(
            ctx,
            async (flip, ct) =>
            {
                if (admissionKind != AdmissionKind.Unconditional
                    && await jobRepository.AnyActiveTransitionJobAsync(
                        ctx.InstanceId,
                        JobType.AsyncTransition,
                        jobName.SourceState,
                        ctx.TransitionKey,
                        ct))
                {
                    logger.TransitionJobAlreadyQueued(jobName.Value, ctx.InstanceId, ctx.TransitionKey);
                    return Result.Fail(
                        WorkflowErrors.TransitionJobAlreadyActive(ctx.InstanceId, ctx.TransitionKey));
                }

                // Inherit the claim: an intermediate relay's own accept classifies as OwnerReentry
                // (it arrives pre-reserved), so admission performs no flip for it. Seeding from the
                // context carries the originating accept's chain reserve through every hop —
                // otherwise the claim is dropped after the first relay and the leaf, which that
                // same accept flipped Busy, rejects the forward with a 409.
                var chainReserved = ctx.SubflowChainReserved || flip == AcceptFlip.ChainReserved;

                var enqueueResult = await EnqueueAndSaveJobAsync(
                    context, ctx, jobName, jobId, activity, chainReserved, ct);

                if (enqueueResult.IsSuccess)
                {
                    armHandle = enqueueResult.Value.ArmHandle;
                    LogEnqueueSuccess(context, jobName.Value);
                    return Result.Ok();
                }

                LogEnqueueFailure(context);
                return Result.Fail(enqueueResult.Error);
            },
            cancellationToken);

        // Arm OUTSIDE the lock: one scheduler call, no database access — the handle carries the
        // payload from the enqueue. Only after a successful accept, so a compensated flip never
        // leaves an armed job behind. A null handle means the outbox relay owns delivery instead.
        if (acceptResult.IsSuccess && armHandle is not null)
        {
            using var armActivity = BackgroundJobActivityHelper.StartArmActivity(jobName);
            try
            {
                await armHandle.ArmAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                armActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            logger.TransitionJobArmedAfterLock(armHandle.JobId);
        }

        var result = acceptResult.IsSuccess
            ? Result<TransitionExecutionContext>.Ok(ctx)
            : Result<TransitionExecutionContext>.Fail(acceptResult.Error);

        SetActivityStatus(activity, result);
        return result;
    }

    /// <summary>
    /// Persists the durable job intent (<see cref="InstanceJob"/>) and delegates the enqueue decision
    /// to <see cref="ITransitionEnqueueGateway"/> — both within a single RequiresNew unit of work so
    /// the intent and the delivery action (Dapr schedule or outbox row) commit atomically.
    /// </summary>
    private async Task<Result<TransitionEnqueueOutcome>> EnqueueAndSaveJobAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        JobName jobName,
        Guid jobId,
        Activity? activity,
        bool subflowChainReserved,
        CancellationToken cancellationToken)
    {
        // The durable half of the accept — job row, delivery decision, commit — runs under the
        // status lock and used to be the unnamed remainder of the server span.
        using var enqueueActivity = PipelineStepActivityHelper.StartTransitionActivity(
            "Transition.Enqueue", transContext.TransitionKey);
        enqueueActivity?.SetTag(TelemetryConstants.TagNames.JobName, jobName.Value);

        var directPayload = BuildDirectPayload(context, transContext, jobName.Value, activity, subflowChainReserved);
        var outboxEvent = BuildOutboxEvent(context, transContext, jobName, jobId, activity, subflowChainReserved);

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        await jobRepository.InsertAsync(
            InstanceJob.Create(jobId, jobName, jobId, context.Domain, context.WorkflowKey, transContext.InstanceId),
            true,
            cancellationToken);

        // deferArming: the row must commit under the status lock so the duplicate-job guard's next
        // reader sees it, but the scheduler round-trip must not — it was the dominant term of the
        // lock hold under load. The caller arms once AcceptAsync has released the lock.
        var outcome = await enqueueGateway.EnqueueAsync(
            directPayload, outboxEvent, deferArming: true, cancellationToken: cancellationToken);

        await uow.CommitAsync(cancellationToken);

        enqueueActivity?.SetTag(TelemetryConstants.TagNames.EnqueuePath, outcome.Path.ToString());
        return Result<TransitionEnqueueOutcome>.Ok(outcome);
    }

    /// <summary>
    /// Builds the payload for the direct Dapr enqueue path.
    /// </summary>
    private static TransitionJobPayload BuildDirectPayload(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        string jobName,
        Activity? activity,
        bool subflowChainReserved)
    {
        return new TransitionJobPayload
        {
            JobName = jobName,
            InstanceId = transContext.InstanceId,
            TransitionKey = transContext.TransitionKey,
            Domain = transContext.Domain,
            Workflow = transContext.WorkflowKey,
            Version = transContext.Workflow.Version,
            Data = context.Data?.Attributes,
            RawBody = null, // raw body is not propagated to the background job
            InstanceKey = context.Data?.Key,
            Tags = context.Data?.Tags,
            Headers = context.Headers,
            RouteValues = context.RouteValues,
            ExecutionActor = context.Actor,
            CallerSync = false,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            // Anchor = the request's lane (the ASP.NET server span), so this hop and every hop the
            // chain adds after it are siblings under the APM transaction rather than nested.
            TraceRoot = WorkflowTraceLane.Current,
            ParentTraceRoot = WorkflowTraceLane.ParentLane,
            ChainDepth = transContext.ChainDepth,
            LaneSeq = WorkflowTraceLane.NextSeq(),
            // The activation episode opened by this request: the job that brings the instance to
            // rest measures from the request's arrival, not from its own start.
            EpisodeStartedAt = WorkflowTraceLane.Episode?.StartedAt,
            EpisodeTrigger = WorkflowTraceLane.Episode?.Trigger,
            EpisodeTransitionKey = WorkflowTraceLane.Episode?.TransitionKey,
            EpisodeTraceRoot = WorkflowTraceLane.Episode?.TraceRoot,
            CorrelationId = transContext.CorrelationId,
            Stage = context.Data?.Stage,
            SubflowChainReserved = subflowChainReserved
        };
    }

    /// <summary>
    /// Builds the outbox event for the transactional outbox path.
    /// </summary>
    private static TransitionContinuationRequested BuildOutboxEvent(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        JobName jobName,
        Guid jobId,
        Activity? activity,
        bool subflowChainReserved)
    {
        return new TransitionContinuationRequested
        {
            InstanceId = transContext.InstanceId,
            Domain = transContext.Domain,
            Flow = transContext.WorkflowKey,
            Version = transContext.Workflow.Version,
            TransitionKey = transContext.TransitionKey,
            JobName = jobName.Value,
            JobId = jobId,
            Data = context.Data?.Attributes,
            InstanceKey = context.Data?.Key,
            Tags = context.Data?.Tags,
            Stage = context.Data?.Stage,
            Headers = context.Headers,
            RouteValues = context.RouteValues,
            ExecutionActor = context.Actor.ToString(),
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            TraceRoot = WorkflowTraceLane.Current,
            ParentTraceRoot = WorkflowTraceLane.ParentLane,
            LaneSeq = WorkflowTraceLane.NextSeq(),
            EpisodeStartedAt = WorkflowTraceLane.Episode?.StartedAt,
            EpisodeTrigger = WorkflowTraceLane.Episode?.Trigger,
            EpisodeTransitionKey = WorkflowTraceLane.Episode?.TransitionKey,
            EpisodeTraceRoot = WorkflowTraceLane.Episode?.TraceRoot,
            CorrelationId = transContext.CorrelationId,
            ChainDepth = transContext.ChainDepth,
            SubflowChainReserved = subflowChainReserved
        };
    }

    /// <summary>
    /// Logs successful job enqueue.
    /// </summary>
    private void LogEnqueueSuccess(WorkflowExecutionContext context, string jobName)
    {
        logger.TransitionEnqueued(context.TransitionKey, context.InstanceId, jobName);
    }

    /// <summary>
    /// Enriches the activity with telemetry tags and baggage for distributed tracing correlation.
    /// Includes job name for async job correlation.
    /// </summary>
    private static void EnrichTelemetry(
        Activity? activity,
        TransitionExecutionContext ctx,
        string jobName)
    {
        if (activity is null) return;

        // Set tags for current span
        activity.SetTag(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, ctx.Workflow.Key);
        activity.SetTag(TelemetryConstants.TagNames.FlowVersion, ctx.Workflow.Version);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, ctx.InstanceId);
        activity.SetTag(TelemetryConstants.TagNames.WorkflowInstanceId, ctx.InstanceId.ToString("D").ToLowerInvariant());
        activity.SetTag(TelemetryConstants.TagNames.CorrelationId, ctx.CorrelationId);
        var subject = GetIdentityClaim(ctx.Headers, TelemetryConstants.HeaderNames.Sub);
        var actSub = GetIdentityClaim(ctx.Headers, TelemetryConstants.HeaderNames.ActSub);
        activity.SetTag(TelemetryConstants.TagNames.Sub, subject);
        activity.SetTag(TelemetryConstants.TagNames.ActSub, actSub);
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, ctx.TransitionKey);
        activity.SetTag(TelemetryConstants.TagNames.JobName, jobName);

        // Set baggage for propagation across service boundaries
        activity.SetBaggage(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
        activity.SetBaggage(TelemetryConstants.TagNames.Flow, ctx.Workflow.Key);
        activity.SetBaggage(TelemetryConstants.TagNames.FlowVersion, ctx.Workflow.Version);
        activity.SetBaggage(TelemetryConstants.TagNames.InstanceId, ctx.InstanceId.ToString());
        activity.SetBaggage(TelemetryConstants.TagNames.WorkflowInstanceId, ctx.InstanceId.ToString("D").ToLowerInvariant());
        activity.SetBaggage(TelemetryConstants.TagNames.CorrelationId, ctx.CorrelationId);
        if (subject is not null)
        {
            activity.SetBaggage(TelemetryConstants.TagNames.Sub, subject);
        }
        if (actSub is not null)
        {
            activity.SetBaggage(TelemetryConstants.TagNames.ActSub, actSub);
        }
        activity.SetBaggage(TelemetryConstants.TagNames.TransitionKey, ctx.TransitionKey);
        activity.SetBaggage(TelemetryConstants.TagNames.JobName, jobName);
    }

    private static string? GetIdentityClaim(
        IReadOnlyDictionary<string, string?> headers,
        string headerName)
    {
        var rawValue = headers
            .FirstOrDefault(header => string.Equals(
                header.Key,
                headerName,
                StringComparison.OrdinalIgnoreCase))
            .Value;
        return TelemetryConstants.TryNormalizeIdentityClaim(rawValue, out var normalized)
            ? normalized
            : null;
    }

    /// <summary>
    /// Sets activity status based on result.
    /// </summary>
    private static void SetActivityStatus<T>(Activity? activity, Result<T> result)
    {
        if (activity is null) return;

        if (result.IsSuccess)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            SetActivityError(activity, result.Error);
        }
    }

    /// <summary>
    /// Sets activity error status with error details.
    /// </summary>
    private static void SetActivityError(Activity? activity, Error error)
    {
        if (activity is null) return;

        activity.SetStatus(ActivityStatusCode.Error, error.Message);
        activity.AddTag("error.code", error.Code);
    }

    /// <summary>
    /// Logs failed job enqueue.
    /// </summary>
    private void LogEnqueueFailure(WorkflowExecutionContext context)
    {
        logger.TransitionEnqueueFailed(context.TransitionKey, context.InstanceId);
    }
}
