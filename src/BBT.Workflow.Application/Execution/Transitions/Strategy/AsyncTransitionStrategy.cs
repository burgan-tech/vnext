using System.Diagnostics;
using BBT.Aether.Aspects;
using BBT.Aether.BackgroundJob;
using BBT.Aether.DistributedLock;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Strategies;

/// <summary>
/// Asynchronous transition execution strategy.
/// Executes transitions as background jobs for better scalability and fault tolerance.
/// Acquires a distributed lock before processing to prevent concurrent enqueuing for
/// the same instance. Reserved transitions (cancel, exit, updateData, timeout, subflow
/// resume, shared) lock on their own type-scoped key independent of the base instance
/// lock — so they are accepted and enqueued even while the main flow is Busy, mirroring
/// the sync <see cref="Pipeline.TransitionPipeline"/>. Under the lock, checks if an active
/// job already exists and returns 409 if so. Sets the instance to Busy before enqueueing so callers immediately see
/// the correct in-progress status.
/// <para>
/// Continuation/enqueue atomicity is governed by <c>WorkflowExecutionOptions.UseOutboxContinuations</c>:
/// when ON, the durable intent + a <c>TransitionContinuationRequested</c> outbox row commit in one
/// unit of work and the Inbox performs the Dapr enqueue (fully transactional). When OFF (default),
/// the durable <c>InstanceJob</c> intent is committed in its own unit of work FIRST and the Dapr
/// enqueue happens AFTER — so a failed commit can never orphan an enqueued job; a crash in the
/// narrow window between the intent commit and the enqueue is recovered by the ChainReaper.
/// </para>
/// </summary>
public sealed class AsyncTransitionStrategy(
    IBackgroundJobService backgroundJobService,
    ITransitionContextFactory ctxFactory,
    IInstanceJobRepository jobRepository,
    IInstanceRepository instanceRepository,
    IDistributedLockService distributedLockService,
    IReservedTransitionResolver reservedTransitionResolver,
    ITransitionValidationService validationService,
    IUnitOfWorkManager uowManager,
    IInstanceCommandGateway instanceCommandGateway,
    IDistributedEventBus eventBus,
    IOptions<WorkflowExecutionOptions> executionOptions,
    IRequestRawBodyProvider rawBodyProvider,
    ILogger<AsyncTransitionStrategy> logger) : ITransitionStrategy
{
    /// <summary>
    /// Lock lease duration in seconds — covers the check + enqueue + UoW commit cycle.
    /// </summary>
    private const int DefaultLockLeaseSeconds = 30;

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
        var jobName = JobName.ForAsyncTransition(context.InstanceId, context.TransitionKey).Value;
        EnrichTelemetry(activity, ctx, jobName);

        Result<TransitionExecutionContext> lockScopeResult =
            Result<TransitionExecutionContext>.Fail(WorkflowErrors.InstanceLockConflict(ctx.InstanceId));

        // Reserved transitions (cancel, exit, updateData, timeout, subflow resume, shared)
        // lock on their own type-scoped key — independent of the base instance lock — so the
        // request is accepted and enqueued even while the main flow holds the instance lock.
        // Mirrors TransitionPipeline.RunAsync; execution safety is enforced when the job runs
        // through the sync pipeline.
        var lockKey = reservedTransitionResolver.IsReserved(ctx)
            ? reservedTransitionResolver.GetOwnLockKey(ctx)
            : ctx.LockKey;

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

                await SetInstanceBusyAsync(ctx, cancellationToken);

                var enqueueResult = await EnqueueAndSaveJobAsync(context, ctx, activity, cancellationToken);
                lockScopeResult = enqueueResult.Match(
                    onSuccess: _ =>
                    {
                        LogEnqueueSuccess(context, jobName);
                        return Result<TransitionExecutionContext>.Ok(ctx);
                    },
                    onFailure: error =>
                    {
                        LogEnqueueFailure(context);
                        return Result<TransitionExecutionContext>.Fail(error);
                    });
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
    /// Marks the instance as Busy and persists it within the ambient UoW.
    /// Skips self-marking silently when the instance is already Busy (chained auto transitions),
    /// already Completed, or being resumed from a SubFlow. SubFlow busy propagation runs regardless.
    /// </summary>
    private async Task SetInstanceBusyAsync(
        TransitionExecutionContext ctx,
        CancellationToken cancellationToken)
    {
        if (!ctx.Instance.IsBusy && !ctx.Instance.IsCompleted && !ctx.Directives.IsInternalResume)
        {
            await using var innerUow = await uowManager.BeginAsync(
                new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, cancellationToken);

            ctx.Instance.Busy();
            await instanceRepository.UpdateAsync(ctx.Instance, false, cancellationToken);
            await innerUow.CommitAsync(cancellationToken);
            logger.InstanceSetBusyForAsyncTransition(ctx.InstanceId, ctx.TransitionKey);
        }

        var subflow = ctx.Instance.Subflow;
        if (subflow is null)
            return;

        var markBusyResult = await instanceCommandGateway.MarkBusyAsync(new MarkBusyInput
        {
            Domain = subflow.SubFlowDomain,
            Workflow = subflow.SubFlowName,
            InstanceId = subflow.SubFlowInstanceId,
            Version = subflow.SubFlowVersion
        }, cancellationToken);

        if (!markBusyResult.IsSuccess)
            logger.SubFlowBusyPropagationFailedForAsyncTransition(ctx.InstanceId, subflow.SubFlowInstanceId,
                markBusyResult.Error.Message);
    }

    /// <summary>
    /// Enqueues the transition job to Dapr and saves the job record.
    /// Railway chain: Build Payload → Enqueue to Dapr → Save Record
    /// </summary>
    private async Task<Result<string>> EnqueueAndSaveJobAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        // S4: when outbox-routed continuations are enabled, persist the durable job intent and
        // publish a TransitionContinuationRequested event ATOMICALLY (same UoW). The actual Dapr
        // enqueue is performed by the Inbox handler — closing the dual-write gap. Otherwise keep
        // the legacy pre-commit Dapr enqueue.
        if (executionOptions.Value.UseOutboxContinuations)
            return await PublishContinuationViaOutboxAsync(context, transContext, activity, cancellationToken);

        // OFF (direct) path — intent-first ordering for atomicity:
        // 1) Commit the durable InstanceJob intent in its OWN unit of work, THEN
        // 2) enqueue to Dapr.
        // This closes the dual-write gap: a Dapr job can never exist without a tracking
        // InstanceJob (the previous order — enqueue then commit — could orphan a job if the
        // commit failed). If the process crashes after the intent commit but before the
        // enqueue, the ChainReaper backstop recovers the pending intent. The job is keyed by
        // JobName (not the Dapr-returned id), so a generated id for the intent is sufficient —
        // mirroring the outbox path.
        var (jobName, jobPayload, schedule, metadata) =
            BuildJobPayload(context, transContext, activity, rawBodyProvider.GetRawBody());
        var jobId = Guid.NewGuid();

        await using (var jobUow = await uowManager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }, cancellationToken))
        {
            await SaveJobRecordAsync(context, transContext, jobName, jobId, cancellationToken);
            await jobUow.CommitAsync(cancellationToken);
        }

        // Side-effect AFTER the intent is durable. Dapr is external → TryAsync.
        var enqueueResult = await EnqueueToDaprAsync(jobName.Value, jobPayload, schedule, metadata, cancellationToken);
        if (!enqueueResult.IsSuccess)
            return Result<string>.Fail(enqueueResult.Error);

        return Result<string>.Ok(jobName.Value);
    }

    /// <summary>
    /// Persists the durable job intent (<see cref="InstanceJob"/>) and publishes a
    /// <see cref="TransitionContinuationRequested"/> event through the transactional outbox
    /// within a single unit of work, so the intent and the outbox row commit atomically.
    /// The Inbox handler then performs the real Dapr enqueue (at-least-once).
    /// </summary>
    private async Task<Result<string>> PublishContinuationViaOutboxAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var jobName = JobName.ForAsyncTransition(transContext.InstanceId, transContext.TransitionKey);

        // The active-job guard keys on JobName, so a generated job id is sufficient here;
        // the real Dapr job id is produced later by the Inbox handler's enqueue.
        var jobId = Guid.NewGuid();

        var continuation = new TransitionContinuationRequested
        {
            InstanceId = transContext.InstanceId,
            Domain = transContext.Domain,
            Flow = transContext.WorkflowKey,
            Version = transContext.Workflow.Version,
            TransitionKey = transContext.TransitionKey,
            JobName = jobName.Value,
            Data = context.Data?.Attributes,
            InstanceKey = context.Data?.Key,
            Tags = context.Data?.Tags,
            Stage = context.Data?.Stage,
            Headers = context.Headers,
            RouteValues = context.RouteValues,
            ExecutionActor = context.Actor.ToString(),
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            ChainToken = transContext.ChainToken, // propagate chain ownership (S6)
            ChainDepth = transContext.ChainDepth
        };

        await using var uow = await uowManager.BeginAsync(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }, cancellationToken);

        await SaveJobRecordAsync(context, transContext, jobName, jobId, cancellationToken);
        await eventBus.PublishAsync(continuation, subject: null, useOutbox: true, cancellationToken);

        await uow.CommitAsync(cancellationToken);

        return Result<string>.Ok(jobName.Value);
    }

    /// <summary>
    /// Enqueues the job to Dapr background job service.
    /// Uses TryAsync because Dapr is an external service.
    /// </summary>
    private Task<Result<Guid>> EnqueueToDaprAsync(
        string jobName,
        TransitionJobPayload jobPayload,
        string schedule,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken)
    {
        var fp = executionOptions.Value.FailurePolicy;
        var failurePolicy = JobScheduleFailurePolicy.Constant(
            TimeSpan.FromSeconds(fp.IntervalSeconds),
            (uint)fp.MaxRetries);

        return ResultExtensions.TryAsync(
            async ct => await backgroundJobService.EnqueueAsync(
                TransitionJobHandler.HandlerName,
                jobName,
                jobPayload,
                schedule,
                metadata,
                failurePolicy,
                ct),
            cancellationToken,
            ex => Error.Dependency(
                WorkflowErrorCodes.Dependency,
                $"Failed to enqueue transition job '{jobName}': {ex.Message}",
                "Dapr"));
    }

    /// <summary>
    /// Saves the job record to the repository.
    /// No Try wrapper - repository exceptions bubble up to middleware.
    /// </summary>
    private Task SaveJobRecordAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        JobName jobName,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        return jobRepository.InsertAsync(
            InstanceJob.Create(
                jobId,
                jobName,
                jobId,
                context.Domain,
                context.WorkflowKey,
                transContext.InstanceId),
            true,
            cancellationToken);
    }

    /// <summary>
    /// Builds the job payload, schedule, and metadata.
    /// Pure function - no side effects.
    /// </summary>
    private static (JobName JobName, TransitionJobPayload Payload, string Schedule, Dictionary<string, object> Metadata)
        BuildJobPayload(WorkflowExecutionContext context, TransitionExecutionContext transContext, Activity? activity,
            string? rawBody)
    {
        var jobName = JobName.ForAsyncTransition(transContext.InstanceId, transContext.TransitionKey);

        var jobPayload = new TransitionJobPayload
        {
            JobName = jobName.Value,
            InstanceId = transContext.InstanceId,
            TransitionKey = transContext.TransitionKey,
            Domain = transContext.Domain,
            Workflow = transContext.WorkflowKey,
            Version = transContext.Workflow.Version,
            Data = context.Data?.Attributes,
            RawBody = rawBody,
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

        var schedule = DaprJobSchedule.FromDateTime(DateTime.UtcNow.AddMilliseconds(5)).ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = context.Domain,
            ["flowName"] = context.WorkflowKey,
            ["instanceId"] = context.InstanceId.ToString()
        };

        // Add trace context to metadata for Dapr job correlation
        if (activity?.TraceId.ToString() is { } traceId)
        {
            metadata["traceId"] = traceId;
        }

        return (jobName, jobPayload, schedule, metadata);
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