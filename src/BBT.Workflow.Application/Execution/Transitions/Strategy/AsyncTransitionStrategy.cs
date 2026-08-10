using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Strategies;

/// <summary>
/// Asynchronous transition execution strategy.
/// Executes transitions as background jobs for better scalability and fault tolerance.
/// Acquires a distributed lock before processing to prevent concurrent enqueuing for
/// the same instance. The enqueue lock uses an <see cref="EnqueueLockSuffix"/>-scoped key
/// (accept scope) that is distinct from the execution lock key the job consumer acquires
/// in <see cref="Pipeline.TransitionPipeline"/> — the Dapr job fires ~5ms after scheduling,
/// while this lock is still held, so sharing the key would make the consumer's non-blocking
/// acquire fail (race condition). Reserved transitions (cancel, exit, updateData, timeout,
/// subflow resume, shared) scope the suffix onto their own type-specific key so they are
/// accepted and enqueued even while the main flow is Busy, mirroring the sync pipeline.
/// Under the lock, checks if an active job already exists (409) and rejects non-reserved
/// requests when the instance is already Busy (409) — the explicit replacement for the
/// implicit rejection the previously shared lock key provided. Sets the instance to Busy
/// before enqueueing so callers immediately see the correct in-progress status.
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
    IDistributedLockService distributedLockService,
    IReservedTransitionResolver reservedTransitionResolver,
    ITransitionValidationService validationService,
    IUnitOfWorkManager uowManager,
    ITransitionAdmissionService admissionService,
    ITransitionEnqueueGateway enqueueGateway,
    ILogger<AsyncTransitionStrategy> logger) : ITransitionStrategy
{
    /// <summary>
    /// Lock lease duration in seconds — covers the check + enqueue + UoW commit cycle.
    /// </summary>
    private const int DefaultLockLeaseSeconds = 30;

    /// <summary>
    /// Suffix scoping the accept/enqueue lock away from the execution lock key used by the
    /// job consumer (TransitionPipeline). Must never be used on the consumer side.
    /// </summary>
    public const string EnqueueLockSuffix = ":enqueue";

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
    [Trace]
    public Task<Result<TransitionExecutionContext>> ExecuteAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;
        return ctxFactory.CreateAsync(context, cancellationToken)
            .BindAsync(ctx => ValidateAsync(ctx, cancellationToken))
            .BindAsync(ctx => EnqueueJobAndReturnContextAsync(ctx, context, activity, cancellationToken));
    }

    /// <summary>
    /// Validates the transition context (schema + state-machine policy) before
    /// any side effects (Busy flip, lock acquisition, job enqueue).
    /// Mirrors the guard in <c>TransitionPipeline.RunAsync</c> for the sync path.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> ValidateAsync(
        TransitionExecutionContext ctx,
        CancellationToken cancellationToken)
    {
        var validationResult = await validationService.ValidateAsync(ctx, cancellationToken);
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
        // Source-state key scopes the job name (see JobName). Must match the value used when the job
        // is persisted in EnqueueAndSaveJobAsync so the AnyActiveByJobNameAsync guard below lines up.
        var jobName = JobName.ForAsyncTransition(
            Guid.Parse(context.InstanceId), ctx.Current?.Key ?? string.Empty, context.TransitionKey).Value;
        EnrichTelemetry(activity, ctx, jobName);

        Result<TransitionExecutionContext> lockScopeResult =
            Result<TransitionExecutionContext>.Fail(WorkflowErrors.InstanceLockConflict(ctx.InstanceId));

        // Reserved transitions (cancel, exit, updateData, timeout, subflow resume, shared)
        // scope onto their own type-specific key so the request is accepted and enqueued even
        // while the main flow is Busy. The EnqueueLockSuffix keeps this accept-scope lock
        // distinct from the execution lock the job consumer acquires in TransitionPipeline —
        // the Dapr job fires while this lock is still held, so sharing the key would race.
        var isReserved = reservedTransitionResolver.IsReserved(ctx);
        var lockKey = (isReserved
            ? reservedTransitionResolver.GetOwnLockKey(ctx)
            : ctx.LockKey) + EnqueueLockSuffix;

        var lockAcquired = await distributedLockService.ExecuteWithLockAsync(
            lockKey,
            async () =>
            {
                if (await jobRepository.AnyActiveByJobNameAsync(ctx.InstanceId, jobName, cancellationToken))
                {
                    logger.TransitionJobAlreadyQueued(jobName, ctx.InstanceId, ctx.TransitionKey);
                    lockScopeResult = Result<TransitionExecutionContext>.Fail(
                        WorkflowErrors.TransitionJobAlreadyActive(ctx.InstanceId, ctx.TransitionKey));
                    return;
                }

                var reserved = false;

                // Busy-as-mutex accept: Normal requests reserve the instance NOW (Busy under
                // the short status lock) so a competing request gets 409 immediately; the job
                // re-enters as the owner (IsPreReserved). Bypass/unconditional kinds (cancel/
                // exit/timeout/updateData) are accepted without a reserve — the job's pipeline
                // prologue admits them by kind.
                if (!ctx.Directives.IsInternalResume
                    && admissionService.Classify(ctx) == AdmissionKind.Normal)
                {
                    var admission = admissionService.CheckAdmission(ctx);
                    if (!admission.IsSuccess)
                    {
                        lockScopeResult = Result<TransitionExecutionContext>.Fail(admission.Error);
                        return;
                    }

                    var reserve = await admissionService.ReserveAsync(ctx, cancellationToken);
                    if (!reserve.IsSuccess)
                    {
                        lockScopeResult = Result<TransitionExecutionContext>.Fail(reserve.Error);
                        return;
                    }

                    reserved = true;
                }

                var enqueueResult = await EnqueueAndSaveJobAsync(context, ctx, activity, cancellationToken);
                if (enqueueResult.IsSuccess)
                {
                    LogEnqueueSuccess(context, jobName);
                    lockScopeResult = Result<TransitionExecutionContext>.Ok(ctx);
                }
                else
                {
                    LogEnqueueFailure(context);

                    // Compensate a reserve whose job never made it to the queue — otherwise the
                    // instance stays Busy with no job to settle it.
                    if (reserved)
                        await admissionService.ReleaseReservationAsync(ctx, cancellationToken);

                    lockScopeResult = Result<TransitionExecutionContext>.Fail(enqueueResult.Error);
                }
            },
            DefaultLockLeaseSeconds,
            cancellationToken);

        if (!lockAcquired)
        {
            logger.InstanceLockFailed(ctx.InstanceId.ToString());
            return Result<TransitionExecutionContext>.Fail(WorkflowErrors.InstanceLockConflict(ctx.InstanceId));
        }

        SetActivityStatus(activity, lockScopeResult);
        return lockScopeResult;
    }

    /// <summary>
    /// Persists the durable job intent (<see cref="InstanceJob"/>) and delegates the enqueue decision
    /// to <see cref="ITransitionEnqueueGateway"/> — both within a single RequiresNew unit of work so
    /// the intent and the delivery action (Dapr schedule or outbox row) commit atomically.
    /// </summary>
    private async Task<Result<string>> EnqueueAndSaveJobAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var jobName = JobName.ForAsyncTransition(
            transContext.InstanceId, transContext.Current?.Key ?? string.Empty, transContext.TransitionKey);
        var jobId = Guid.NewGuid();

        var directPayload = BuildDirectPayload(context, transContext, jobName.Value, activity);
        var outboxEvent = BuildOutboxEvent(context, transContext, jobName, jobId, activity);

        await using var uow = uowManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

        await jobRepository.InsertAsync(
            InstanceJob.Create(jobId, jobName, jobId, context.Domain, context.WorkflowKey, transContext.InstanceId),
            true,
            cancellationToken);

        await enqueueGateway.EnqueueAsync(directPayload, outboxEvent, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return Result<string>.Ok(jobName.Value);
    }

    /// <summary>
    /// Builds the payload for the direct Dapr enqueue path.
    /// </summary>
    private static TransitionJobPayload BuildDirectPayload(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        string jobName,
        Activity? activity)
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
            Stage = context.Data?.Stage
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
        Activity? activity)
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
            ChainDepth = transContext.ChainDepth
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
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, ctx.TransitionKey);
        activity.SetTag(TelemetryConstants.TagNames.JobName, jobName);

        // Set baggage for propagation across service boundaries
        activity.SetBaggage(TelemetryConstants.TagNames.Domain, ctx.Workflow.Domain);
        activity.SetBaggage(TelemetryConstants.TagNames.Flow, ctx.Workflow.Key);
        activity.SetBaggage(TelemetryConstants.TagNames.FlowVersion, ctx.Workflow.Version);
        activity.SetBaggage(TelemetryConstants.TagNames.InstanceId, ctx.InstanceId.ToString());
        activity.SetBaggage(TelemetryConstants.TagNames.TransitionKey, ctx.TransitionKey);
        activity.SetBaggage(TelemetryConstants.TagNames.JobName, jobName);
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
