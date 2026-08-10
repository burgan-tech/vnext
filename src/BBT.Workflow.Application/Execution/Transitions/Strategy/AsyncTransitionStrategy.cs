using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BBT.Aether.Aspects;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Continuations;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

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
/// Under the lock, reserved commands retain their active-job guard and normal requests use the
/// atomic Busy/revision reservation (409 on contention). Active-SubFlow proxy requests reuse the
/// parent's chain token and are forwarded after commit. The instance becomes Busy before enqueue
/// so callers immediately see the correct in-progress status.
/// <para>
/// Enqueue atomicity is governed by <c>WorkflowExecutionOptions.DirectEnqueueContinuations</c>:
/// when ON (default), the durable intent + Aether scheduler row commit in one unit of work.
/// Synchronous scheduler-staging failure falls back to the transactional outbox; post-commit arm
/// retries belong to Aether. When OFF, the outbox path is always used — the Inbox performs the
/// Dapr enqueue (at the cost of the outbox/inbox poll hop). Both paths use the same
/// <see cref="ITransitionEnqueueGateway"/>.
/// </para>
/// </summary>
public sealed class AsyncTransitionStrategy(
    ITransitionContextFactory ctxFactory,
    IInstanceJobRepository jobRepository,
    IDistributedLockService distributedLockService,
    IReservedTransitionResolver reservedTransitionResolver,
    ITransitionValidationService validationService,
    IInstanceRepository instanceRepository,
    ITransitionEnqueueGateway enqueueGateway,
    IRequestRawBodyProvider rawBodyProvider,
    ITransitionCommitLeaseManager commitLeaseManager,
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
    /// Railway chain: Create Context → Validate Schema → Lock → Reload + Validate Policy →
    /// Set Busy → Enqueue Job → Return Context.
    /// </summary>
    /// <remarks>
    /// Immutable input-schema validation runs before the lock so bad requests create no side
    /// effects. State policy runs after a fresh lock-protected reload, preventing a snapshot that
    /// became stale while waiting from producing an optimistic-concurrency exception.
    /// </remarks>
    [Trace]
    public Task<Result<TransitionExecutionContext>> ExecuteAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;
        return ctxFactory.CreateAsync(context, cancellationToken)
            .BindAsync(ctx => ValidateSchemaBeforeLockAsync(ctx, context, cancellationToken))
            .BindAsync(ctx => EnqueueJobAndReturnContextAsync(ctx, context, activity, cancellationToken));
    }

    /// <summary>
    /// Validates only the immutable request schema before lock acquisition. State-machine policy
    /// is checked against the authoritative snapshot inside the lock.
    /// </summary>
    private async Task<Result<TransitionExecutionContext>> ValidateSchemaBeforeLockAsync(
        TransitionExecutionContext ctx,
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        var validationResult = workflowContext.TransitionSchemaValidated
            ? Result.Ok()
            : await validationService.ValidateInputSchemaAsync(ctx, cancellationToken);
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

        var idempotencyKey = GetIdempotencyKey(context.Headers);
        if (idempotencyKey?.Length > InstanceJobConstants.MaxIdempotencyKeyLength)
        {
            return Result<TransitionExecutionContext>.Fail(
                WorkflowErrors.InvalidIdempotencyKey(InstanceJobConstants.MaxIdempotencyKeyLength));
        }

        // Reserved transitions (cancel, exit, updateData, timeout, subflow resume, shared)
        // scope onto their own type-specific key so the request is accepted and enqueued even
        // while the main flow is Busy. The EnqueueLockSuffix keeps this accept-scope lock
        // distinct from the execution lock the job consumer acquires in TransitionPipeline —
        // the Dapr job fires while this lock is still held, so sharing the key would race.
        var isReserved = reservedTransitionResolver.IsReserved(ctx);
        var forwardsToActiveSubflow = ctx.ForwardsToActiveSubflow();
        // The idempotency uniqueness constraint is instance-wide. Requests carrying the same key
        // must therefore share one admission lock even when they target different reserved
        // transition types; otherwise both can observe lookup-miss and race into a unique-index
        // exception instead of a deterministic replay/conflict response.
        var lockKey = idempotencyKey is null
            ? (isReserved
                ? reservedTransitionResolver.GetOwnLockKey(ctx)
                : ctx.LockKey) + EnqueueLockSuffix
            : $"{ctx.LockKey}{EnqueueLockSuffix}:idempotency:{HashLockKey(idempotencyKey)}";

        var lockHandle = await distributedLockService.TryAcquireLockAsync(
            lockKey,
            DefaultLockLeaseSeconds,
            cancellationToken);
        if (lockHandle is null)
        {
            logger.InstanceLockFailed(ctx.InstanceId.ToString());
            return Result<TransitionExecutionContext>.Fail(WorkflowErrors.InstanceLockConflict(ctx.InstanceId));
        }

        var leaseTransferred = false;
        try
        {
            Result<TransitionExecutionContext> lockScopeResult;

            // The preflight entity may have gone stale while this request waited for the enqueue
            // lock. Replace it with one authoritative tracked aggregate before checking Busy,
            // state policy, idempotency or writing the reservation. This turns ordinary request
            // contention into deterministic Busy/state responses instead of a revision conflict
            // during SaveChanges.
            var authoritativeResult = await ReloadAndValidatePolicyAsync(
                ctx,
                context,
                cancellationToken);
            if (!authoritativeResult.IsSuccess)
            {
                lockScopeResult = Result<TransitionExecutionContext>.Fail(authoritativeResult.Error);
                SetActivityStatus(activity, lockScopeResult);
                return lockScopeResult;
            }

            ctx = authoritativeResult.Value!;

            // Normal transitions are serialized by the Busy concurrency update. Reserved
            // transitions may legitimately run while Busy, so retain the active-job guard only
            // for them.
            if (isReserved && await jobRepository.AnyActiveByJobNameAsync(
                    ctx.InstanceId, jobName, cancellationToken))
            {
                logger.TransitionJobAlreadyQueued(jobName, ctx.InstanceId, ctx.TransitionKey);
                lockScopeResult = Result<TransitionExecutionContext>.Fail(
                    WorkflowErrors.TransitionJobAlreadyActive(ctx.InstanceId, ctx.TransitionKey));
            }
            else
            {
                var enqueueResult = await AdmitAndEnqueueAsync(
                    context,
                    ctx,
                    isReserved,
                    forwardsToActiveSubflow,
                    idempotencyKey,
                    activity,
                    cancellationToken);
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
            }

            if (lockScopeResult.IsSuccess)
            {
                // Runner owns the ambient UoW. Transfer the lease so it is released only after
                // Busy + InstanceJob + scheduler/outbox state has committed atomically.
                commitLeaseManager.Hold(lockHandle);
                leaseTransferred = true;
            }

            SetActivityStatus(activity, lockScopeResult);
            return lockScopeResult;
        }
        finally
        {
            if (!leaseTransferred)
                await lockHandle.DisposeAsync();
        }
    }

    private async Task<Result<TransitionExecutionContext>> ReloadAndValidatePolicyAsync(
        TransitionExecutionContext preflightContext,
        WorkflowExecutionContext workflowContext,
        CancellationToken cancellationToken)
    {
        var instanceResult = await instanceRepository.ReloadActiveAsync(
            workflowContext.InstanceId,
            cancellationToken);
        if (!instanceResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(instanceResult.Error);

        var contextResult = ctxFactory.CreateFromPreloaded(
            workflowContext,
            preflightContext.Workflow,
            instanceResult.Value!);
        if (!contextResult.IsSuccess)
            return Result<TransitionExecutionContext>.Fail(contextResult.Error);

        var authoritativeContext = contextResult.Value!;
        var policyResult = await validationService.ValidatePolicyAsync(
            authoritativeContext,
            cancellationToken);
        return policyResult.IsSuccess
            ? Result<TransitionExecutionContext>.Ok(authoritativeContext)
            : Result<TransitionExecutionContext>.Fail(policyResult.Error);
    }

    /// <summary>
    /// Persists the durable job intent (<see cref="InstanceJob"/>) and delegates the enqueue decision
    /// to <see cref="ITransitionEnqueueGateway"/> — both within a single RequiresNew unit of work so
    /// the intent and the delivery action (Dapr schedule or outbox row) commit atomically.
    /// </summary>
    private async Task<Result<string>> AdmitAndEnqueueAsync(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        bool isReserved,
        bool forwardsToActiveSubflow,
        string? idempotencyKey,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var jobName = JobName.ForAsyncTransition(
            transContext.InstanceId, transContext.Current?.Key ?? string.Empty, transContext.TransitionKey);
        var jobId = Guid.NewGuid();
        var rawBody = rawBodyProvider.GetRawBody();

        var requestFingerprint = CreateRequestFingerprint(context, rawBody);

        // TransitionRunner already opened the authoritative stage UoW and the context factory
        // loaded this tracked aggregate inside it. Reuse that instance so reservation + job intent
        // + scheduler row commit in one batch without a second SELECT or nested transaction.
        var instance = transContext.Instance;

        // A repeated request with the same key returns the already-admitted outcome and never
        // creates a second job. The fingerprint prevents accidental key reuse for another body.
        if (idempotencyKey is not null)
        {
            var existingJob = await jobRepository.FindByIdempotencyKeyAsReadOnlyAsync(
                instance.Id,
                idempotencyKey,
                cancellationToken);
            if (existingJob is not null)
            {
                if (!string.Equals(
                        existingJob.RequestFingerprint,
                        requestFingerprint,
                        StringComparison.Ordinal))
                {
                    return Result<string>.Fail(
                        WorkflowErrors.IdempotencyKeyConflict(instance.Id, idempotencyKey));
                }

                transContext.ChainToken = existingJob.AdmissionToken;
                transContext.ExpectedRevision = existingJob.AdmittedRevision;
                transContext.ClientResponse = new ClientResponse
                {
                    Id = instance.Id,
                    Status = existingJob.IsActive ? InstanceStatus.Busy : instance.Status
                };
                return Result<string>.Ok(existingJob.JobName);
            }
        }

        if (instance.IsCompleted)
        {
            return Result<string>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceCompleted,
                $"Instance '{instance.Id}' is already terminal.",
                instance.Id.ToString()));
        }

        var instanceMutated = false;
        Guid admissionToken;
        if (transContext.Directives.IsInternalResume)
        {
            admissionToken = transContext.ChainToken
                             ?? instance.ChainToken
                             ?? Guid.NewGuid();
            if (instance.ChainToken.HasValue && instance.ChainToken != admissionToken)
            {
                return Result<string>.Fail(
                    WorkflowErrors.InstanceBusy(instance.Id, transContext.TransitionKey));
            }
        }
        else if (forwardsToActiveSubflow)
        {
            // The parent intentionally remains Busy while its blocking SubFlow owns execution.
            // Queue a proxy hop under the same chain token; ForwardToActiveSubflowStep performs
            // the actual remote call after the admission transaction and parent lock have ended.
            admissionToken = instance.ChainToken ?? Guid.NewGuid();
            if (!instance.MatchesChain(admissionToken))
            {
                instance.BeginChain(admissionToken);
                instanceMutated = true;
            }
        }
        else if (isReserved)
        {
            // Reserved operations may be admitted while Busy but never steal an existing chain.
            admissionToken = instance.ChainToken ?? Guid.NewGuid();
            if (!instance.IsBusy)
            {
                instance.BeginChain(admissionToken);
                instanceMutated = true;
            }
        }
        else
        {
            // SubFlow/SubProcess instances are deliberately persisted as Busy before their start
            // transition is dispatched: the parent owns their lifecycle from the first instant.
            // That prepared row is not an already-running transition, though. Adopt it exactly
            // once by minting the chain token here; all later Busy requests have either a token
            // or a non-zero revision and continue to be rejected.
            var canAdoptPreparedSubItemStart = IsPreparedSubItemStart(transContext);
            if (instance.IsBusy && !canAdoptPreparedSubItemStart)
            {
                logger.AsyncTransitionRejectedInstanceBusy(
                    transContext.TransitionKey,
                    transContext.InstanceId);
                return Result<string>.Fail(
                    WorkflowErrors.InstanceBusy(transContext.InstanceId, transContext.TransitionKey));
            }

            admissionToken = Guid.NewGuid();
            instance.BeginChain(admissionToken);
            instanceMutated = true;
        }

        transContext.ChainToken = admissionToken;
        long? admittedRevision = instanceMutated
            ? instance.Revision + 1
            : forwardsToActiveSubflow
                ? instance.Revision
                : null;
        transContext.ExpectedRevision = admittedRevision;

        var directPayload = BuildDirectPayload(
            context,
            transContext,
            jobName.Value,
            jobId,
            admissionToken,
            admittedRevision,
            rawBody,
            activity);
        var outboxEvent = BuildOutboxEvent(
            context,
            transContext,
            jobName,
            jobId,
            admittedRevision,
            rawBody,
            activity);

        var job = InstanceJob.CreateTransitionAdmission(
            jobId,
            jobName,
            jobId,
            context.Domain,
            context.WorkflowKey,
            transContext.InstanceId,
            JsonSerializer.Serialize(directPayload, JsonSerializerOptions.Web),
            admissionToken,
            admittedRevision,
            idempotencyKey,
            requestFingerprint);

        if (instanceMutated)
            await instanceRepository.UpdateAsync(instance, false, cancellationToken);

        await jobRepository.InsertAsync(job, false, cancellationToken);

        await enqueueGateway.EnqueueAsync(directPayload, outboxEvent, cancellationToken);

        job.MarkAsScheduled();

        // Runner commits the ambient stage UoW after the core strategy returns. The explicit
        // response keeps async HTTP semantics stable without an additional read.
        transContext.ClientResponse = new ClientResponse
        {
            Id = transContext.InstanceId,
            Status = InstanceStatus.Busy
        };

        return Result<string>.Ok(jobName.Value);
    }

    private static bool IsPreparedSubItemStart(TransitionExecutionContext context)
    {
        var instance = context.Instance;
        return instance.IsBusy
               && instance.IsSubItem
               && instance.ChainToken is null
               && instance.Revision == 0
               && string.Equals(
                   context.TransitionKey,
                   context.Workflow.StartTransition.Key,
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the payload for the direct Dapr enqueue path.
    /// </summary>
    private static TransitionJobPayload BuildDirectPayload(
        WorkflowExecutionContext context,
        TransitionExecutionContext transContext,
        string jobName,
        Guid jobId,
        Guid admissionToken,
        long? admittedRevision,
        string? rawBody,
        Activity? activity)
    {
        return new TransitionJobPayload
        {
            JobId = jobId,
            JobName = jobName,
            InstanceId = transContext.InstanceId,
            TransitionKey = transContext.TransitionKey,
            Domain = transContext.Domain,
            Workflow = transContext.WorkflowKey,
            Version = transContext.Workflow.Version,
            Data = context.Data?.Attributes,
            RawBody = rawBody,
            InstanceKey = context.Data?.Key,
            Tags = context.Data?.Tags,
            Headers = DurableHeaderFilter.ForPersistence(context.Headers),
            RouteValues = context.RouteValues,
            ExecutionActor = context.Actor,
            CallerSync = false,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            Stage = context.Data?.Stage,
            ChainToken = admissionToken,
            AdmissionToken = admissionToken,
            AdmittedRevision = admittedRevision,
            TransitionSchemaValidated = true,
            ChainDepth = transContext.ChainDepth,
            TriggerType = context.TriggerType,
            IsReentry = context.IsReentry,
            IsErrorBoundaryTransition = context.IsErrorBoundaryTransition
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
        long? admittedRevision,
        string? rawBody,
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
            RawBody = rawBody,
            InstanceKey = context.Data?.Key,
            Tags = context.Data?.Tags,
            Stage = context.Data?.Stage,
            Headers = DurableHeaderFilter.ForPersistence(context.Headers),
            RouteValues = context.RouteValues,
            ExecutionActor = context.Actor.ToString(),
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            ChainToken = transContext.ChainToken,
            AdmittedRevision = admittedRevision,
            TransitionSchemaValidated = true,
            ChainDepth = transContext.ChainDepth,
            TriggerType = (int)context.TriggerType,
            IsReentry = context.IsReentry,
            IsErrorBoundaryTransition = context.IsErrorBoundaryTransition
        };
    }

    private static string? GetIdempotencyKey(IReadOnlyDictionary<string, string?> headers)
    {
        var value = headers
            .FirstOrDefault(item => string.Equals(
                item.Key,
                "idempotency-key",
                StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim();
    }

    internal static string CreateRequestFingerprint(
        WorkflowExecutionContext context,
        string? rawBody)
    {
        var semanticHeaders = context.Headers
            .Where(item => !IsVolatileIdempotencyHeader(item.Key))
            .Select(item => new KeyValuePair<string, string?>(
                item.Key.ToLowerInvariant(),
                item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToArray();
        var routeValues = context.RouteValues
            .Select(item => new KeyValuePair<string, string?>(
                item.Key.ToLowerInvariant(),
                item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Value, StringComparer.Ordinal)
            .ToArray();

        var source = JsonSerializer.Serialize(new
        {
            context.Domain,
            context.WorkflowKey,
            context.WorkflowVersion,
            context.InstanceId,
            context.TransitionKey,
            Actor = (int)context.Actor,
            RawBody = rawBody,
            DataKey = context.Data?.Key,
            Attributes = context.Data?.Attributes,
            context.Data?.Stage,
            Tags = context.Data?.Tags?.OrderBy(tag => tag, StringComparer.Ordinal).ToArray(),
            Headers = semanticHeaders,
            RouteValues = routeValues
        }, JsonSerializerOptions.Web);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    private static string HashLockKey(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static bool IsVolatileIdempotencyHeader(string key) =>
        key.Equals("idempotency-key", StringComparison.OrdinalIgnoreCase)
        || key.Equals("traceparent", StringComparison.OrdinalIgnoreCase)
        || key.Equals("tracestate", StringComparison.OrdinalIgnoreCase)
        || key.Equals("request-id", StringComparison.OrdinalIgnoreCase)
        || key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase)
        || key.Equals("correlation-id", StringComparison.OrdinalIgnoreCase);

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
