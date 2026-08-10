using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles asynchronous transition background jobs.
/// This handler processes workflow transition requests admitted with Sync=false, reconstructing
/// a forced synchronous execution context so the job cannot enqueue itself recursively.
/// </summary>
public sealed class TransitionJobHandler(
    IInstanceJobRepository jobRepository,
    IWorkflowExecutionService workflowExecutionService,
    ICurrentSchema currentSchema,
    IJobTimeoutRecoveryService recoveryService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    IHostApplicationLifetime hostLifetime,
    ILogger<TransitionJobHandler> logger) : IBackgroundJobHandler<TransitionJobPayload>
{
    public const string HandlerName = "flow.transition";
    private const string InvalidDurablePayloadErrorCode = "JOB_INVALID_DURABLE_PAYLOAD";

    public async Task HandleAsync(TransitionJobPayload args, CancellationToken cancellationToken)
    {
        // Restore trace context from the original request for distributed tracing correlation
        using var activity = BackgroundJobActivityHelper.StartActivityAsChildWithLink("TransitionJob.Execute", args);
        using (currentSchema.Change(args.Workflow))
        {
            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       [TelemetryConstants.TagNames.InstanceId] = args.InstanceId,
                       [TelemetryConstants.TagNames.Flow] = args.Workflow,
                       [TelemetryConstants.TagNames.Domain] = args.Domain,
                       [TelemetryConstants.TagNames.FlowVersion] = args.Version,
                       [TelemetryConstants.TagNames.InstanceKey] = args.InstanceKey ?? "N/A",
                       [TelemetryConstants.TagNames.TransitionKey] = args.TransitionKey,
                       [TelemetryConstants.TagNames.JobName] = args.JobName
                   }))
            {
                var timeoutSeconds = executionOptions.Value.TransitionJobTimeoutSeconds;
                Guid? processingToken = null;

                // Legacy payloads did not carry JobId. New jobs are claimed atomically so Dapr
                // redelivery or parallel delivery cannot execute the same transition twice.
                if (args.JobId != Guid.Empty)
                {
                    var claimToken = Guid.NewGuid();
                    var claimOutcome = await TryClaimAfterAdmissionCommitAsync(
                        args.JobId,
                        claimToken,
                        TimeSpan.FromSeconds(timeoutSeconds + 30),
                        cancellationToken);
                    if (claimOutcome == JobClaimOutcome.AlreadyTerminal)
                    {
                        logger.LogInformation(
                            "Transition job {JobId} was already completed; delivery is a no-op",
                            args.JobId);
                        return;
                    }

                    if (claimOutcome == JobClaimOutcome.RetryRequired)
                    {
                        // A normal return acknowledges a one-shot Dapr delivery as successful.
                        // When an active processing lease is still held (for example after a host
                        // crash), that would permanently consume the only redelivery before the
                        // lease becomes reclaimable. Throw a non-cancellation exception so the
                        // background-job dispatcher records a retry instead.
                        throw new InvalidOperationException(
                            $"Transition job '{args.JobId}' is active but its processing lease is not reclaimable yet.");
                    }

                    processingToken = claimToken;

                    // Once the claim is ours, do not let request cancellation escape as an OCE
                    // between the claim and the protected execution block. Aether treats an OCE
                    // from a handler as terminal cancellation rather than a retry. Host shutdown
                    // is checked explicitly below and releases the fenced claim first.
                    var claimedJob = await jobRepository.FindByJobIdAsReadOnlyAsync(
                        args.JobId,
                        CancellationToken.None);

                    if (hostLifetime.ApplicationStopping.IsCancellationRequested)
                    {
                        var released = await jobRepository.ReleaseClaimAsync(
                            args.JobId,
                            claimToken,
                            CancellationToken.None);
                        if (!released)
                        {
                            logger.LogWarning(
                                "Transition job {JobId} shutdown claim was already lost or terminal",
                                args.JobId);
                        }

                        throw CreateDispatcherRetryException(
                            args.JobId,
                            "host shutdown interrupted execution before canonical payload loading");
                    }

                    if (claimedJob is null)
                    {
                        // TryClaimAsync updated this exact row, so not finding it immediately
                        // afterwards indicates a transient database visibility/integrity problem.
                        // Never fall back to the caller-supplied payload.
                        throw new InvalidOperationException(
                            $"Claimed transition job '{args.JobId}' could not be loaded from durable storage.");
                    }

                    if (!TryReadCanonicalPayload(claimedJob, args, out var canonicalArgs, out var payloadError))
                    {
                        logger.LogError(
                            "Transition job {JobId} has an invalid durable payload: {PayloadError}",
                            args.JobId,
                            payloadError);

                        // The row identity is durable and trusted even when its serialized payload
                        // is corrupt. Recover the reserved instance from that identity, then make
                        // this poison job terminal so it can never execute caller-controlled data.
                        if (!await jobRepository.IsClaimOwnerAsync(
                                claimedJob.JobId,
                                claimToken,
                                CancellationToken.None))
                        {
                            throw CreateDispatcherRetryException(
                                claimedJob.JobId,
                                "claim ownership was lost before invalid-payload recovery");
                        }

                        var recoveryArgs = CreateRecoveryPayload(claimedJob);
                        await recoveryService.FaultInstanceAsync(
                            recoveryArgs,
                            payloadError,
                            InvalidDurablePayloadErrorCode,
                            CancellationToken.None);
                        var finalized = await jobRepository.MarkAsFailedAsync(
                            claimedJob.JobId,
                            claimToken,
                            InvalidDurablePayloadErrorCode,
                            payloadError,
                            CancellationToken.None);
                        if (!finalized)
                        {
                            throw CreateDispatcherRetryException(
                                claimedJob.JobId,
                                "claim ownership was lost during invalid-payload finalization");
                        }
                        return;
                    }

                    // From this point onward all execution, recovery, telemetry and finalization
                    // use the canonical payload committed with the InstanceJob. The delivery body
                    // is only a locator (workflow schema + JobId) and cannot alter execution data.
                    args = canonicalArgs;
                }

                // Separate execution budget CTS from the incoming Dapr/host cancellation token.
                // This lets us distinguish: own timeout vs Dapr HTTP timeout vs host shutdown.
                using var executionCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    executionCts.Token,
                    hostLifetime.ApplicationStopping);

                bool needsRecovery = false;
                bool shouldFinalizeJob = true;
                bool superseded = false;
                bool releaseClaimForRetry = false;
                Exception? dispatcherRetryException = null;
                (string Message, string ErrorCode)? recoveryReason = null;

                try
                {
                    // Expose the original raw request body to mappings built inside this job (no live
                    // HttpContext here) so background signature verification (JWS/mTLS) can run.
                    using var rawBodyScope = RawBodyExecutionScope.Set(args.RawBody);

                    BackgroundJobActivityHelper.EnrichActivity(activity, args);
                    BackgroundJobActivityHelper.EnrichActivityWithTransition(activity, args.TransitionKey);

                    // For async processing, instance should already be pre-reserved and in Busy status
                    // Reconstruct the original TransitionInput with Sync=true
                    var transitionInput = new TransitionInput(
                            args.Domain,
                            args.Workflow,
                            new TransitionDataInput(args.Data)
                            {
                                Key = args.InstanceKey,
                                Tags = args.Tags,
                                Stage = args.Stage
                            },
                            sync: true) // Force sync=true to avoid infinite loop
                        {
                            Headers = args.Headers,
                            RouteValues = args.RouteValues
                        };

                    var context =
                        transitionInput.ToExecutionContext(args.InstanceId.ToString(), args.Version,
                            args.TransitionKey);
                    context.Actor = args.ExecutionActor;
                    context.CallerMode = args.CallerSync ? ExecMode.Sync : ExecMode.Async;
                    context.ChainToken = args.ChainToken;
                    context.ExpectedRevision = args.AdmittedRevision;
                    // Legacy payloads have no durable admission identity and therefore no trusted
                    // schema-validation receipt. Always validate their body again.
                    context.TransitionSchemaValidated =
                        args.JobId != Guid.Empty && args.TransitionSchemaValidated;
                    context.TriggerType = args.TriggerType;
                    context.IsReentry = args.IsReentry;
                    context.IsErrorBoundaryTransition = args.IsErrorBoundaryTransition;
                    context.Execution ??= new ExecutionInfo();
                    context.Execution.ChainDepth = args.ChainDepth;

                    // Transition-per-job: this job runs exactly ONE transition; its auto-chain
                    // continuation is enqueued as the next job via ITransitionEnqueueGateway
                    // instead of running in-process.
                    context.EnqueueContinuations = executionOptions.Value.TransitionPerJob;

                    // Use the background-specific method that handles pre-reserved instances.
                    // Lock conflicts are transient (the enqueue accept lock or a finishing chain
                    // may still hold the execution lock for a few ms) — retry with backoff.
                    var result = await ExecuteWithLockConflictRetryAsync(context, args, linkedCts.Token);

                    if (!result.IsSuccess)
                    {
                        activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
                        logger.JobFailed(args.JobName, args.InstanceId, result.Error.Message ?? "Unknown error");

                        superseded = result.Error.Code == WorkflowErrorCodes.InstanceRevisionConflict;
                        needsRecovery = !superseded;
                        recoveryReason = superseded
                            ? null
                            : (result.Error.Message ?? "Transition job execution failed", result.Error.Code);

                        if (IsLockConflict(result.Error))
                        {
                            // Retries exhausted: do NOT leave the instance silently stranded in Busy —
                            // route it through recovery so it becomes Faulted (visible + retryable).
                            var maxAttempts = executionOptions.Value.LockConflictRetry.MaxAttempts;
                            logger.TransitionJobLockConflictRetriesExhausted(
                                args.JobName, maxAttempts, args.InstanceId, args.TransitionKey);
                            needsRecovery = true;
                            recoveryReason = (
                                $"Transition job could not acquire the instance lock after {maxAttempts} attempts",
                                "JOB_LOCK_CONFLICT");
                        }
                    }
                    else
                    {
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        logger.JobCompleted(args.JobName, args.TransitionKey, args.InstanceId);
                    }
                }
                catch (OperationCanceledException) when (
                    executionCts.IsCancellationRequested
                    && !hostLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // Own 300s execution budget exceeded
                    needsRecovery = true;
                    activity?.SetStatus(ActivityStatusCode.Error, "Job execution timeout");
                    activity?.SetTag("timeout.layer", "job");
                    logger.JobTimedOut(args.JobName, timeoutSeconds, args.TransitionKey, args.InstanceId);
                }
                catch (OperationCanceledException) when (
                    !hostLifetime.ApplicationStopping.IsCancellationRequested)
                {
                    // Dapr HTTP response timeout or external cancellation (not host shutdown)
                    needsRecovery = true;
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled by Dapr/external");
                    activity?.SetTag("timeout.layer", "dapr-cancel");
                    logger.JobCancelledByExternal(args.JobName, args.TransitionKey, args.InstanceId);
                }
                catch (OperationCanceledException)
                {
                    // Host shutdown (SIGTERM) — recovery is not feasible while the process is
                    // stopping. Release only this fenced claim and throw a non-cancellation
                    // exception so Aether records Retrying instead of acknowledging success.
                    shouldFinalizeJob = false;
                    releaseClaimForRetry = true;
                    dispatcherRetryException = CreateDispatcherRetryException(
                        args.JobId,
                        "host shutdown interrupted execution");
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled (host shutdown)");
                    logger.JobCancelled(args.JobName, args.TransitionKey, args.InstanceId);
                }
                catch (Exception e)
                {
                    needsRecovery = true;
                    recoveryReason = (e.Message, "JOB_UNHANDLED_EXCEPTION");
                    activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                    activity?.AddTag("error.type", e.GetType().Name);
                    logger.JobFailed(e, args.JobName, args.InstanceId);
                }
                finally
                {
                    var mayRecover = needsRecovery;
                    if (mayRecover && args.JobId != Guid.Empty)
                    {
                        var token = GetRequiredProcessingToken(args.JobId, processingToken);
                        mayRecover = await jobRepository.IsClaimOwnerAsync(
                            args.JobId,
                            token,
                            CancellationToken.None);
                        if (!mayRecover)
                        {
                            shouldFinalizeJob = false;
                            dispatcherRetryException ??= CreateDispatcherRetryException(
                                args.JobId,
                                "claim ownership was lost before recovery");
                        }
                    }

                    if (mayRecover)
                    {
                        // CancellationToken.None: recovery must complete even if host is shutting down
                        if (recoveryReason is { } reason)
                        {
                            await recoveryService.FaultInstanceAsync(
                                args, reason.Message, reason.ErrorCode, CancellationToken.None);
                        }
                        else
                        {
                            await recoveryService.FaultInstanceAsync(args, CancellationToken.None);
                        }
                    }

                    if (releaseClaimForRetry && args.JobId != Guid.Empty)
                    {
                        var token = GetRequiredProcessingToken(args.JobId, processingToken);
                        var released = await jobRepository.ReleaseClaimAsync(
                            args.JobId,
                            token,
                            CancellationToken.None);
                        if (!released)
                        {
                            logger.LogWarning(
                                "Transition job {JobId} shutdown claim was already lost or terminal",
                                args.JobId);
                        }
                    }

                    if (shouldFinalizeJob && superseded && args.JobId != Guid.Empty)
                    {
                        var token = GetRequiredProcessingToken(args.JobId, processingToken);
                        var finalized = await jobRepository.MarkAsSupersededAsync(
                            args.JobId,
                            token,
                            "Instance revision changed after admission",
                            CancellationToken.None);
                        if (!finalized)
                        {
                            dispatcherRetryException ??= CreateDispatcherRetryException(
                                args.JobId,
                                "claim ownership was lost during supersede finalization");
                        }
                    }
                    else if (shouldFinalizeJob && needsRecovery && args.JobId != Guid.Empty)
                    {
                        var token = GetRequiredProcessingToken(args.JobId, processingToken);
                        var finalized = await jobRepository.MarkAsFailedAsync(
                            args.JobId,
                            token,
                            recoveryReason?.ErrorCode ?? "JOB_EXECUTION_FAILED",
                            recoveryReason?.Message,
                            CancellationToken.None);
                        if (!finalized)
                        {
                            dispatcherRetryException ??= CreateDispatcherRetryException(
                                args.JobId,
                                "claim ownership was lost during failure finalization");
                        }
                    }
                    else if (shouldFinalizeJob)
                    {
                        if (args.JobId != Guid.Empty)
                        {
                            var token = GetRequiredProcessingToken(args.JobId, processingToken);
                            var finalized = await jobRepository.MarkAsProcessedByJobIdAsync(
                                args.JobId,
                                token,
                                CancellationToken.None);
                            if (!finalized)
                            {
                                dispatcherRetryException ??= CreateDispatcherRetryException(
                                    args.JobId,
                                    "claim ownership was lost during success finalization");
                            }
                        }
                        else
                        {
                            // Compatibility for jobs created before JobId was added to the payload.
                            await jobRepository.MarkAsProcessedAsync(
                                args.InstanceId,
                                args.JobName,
                                CancellationToken.None);
                        }
                    }
                }

                if (dispatcherRetryException is not null)
                    throw dispatcherRetryException;
            }
        }
    }

    /// <summary>
    /// A directly scheduled Dapr job can arrive a few milliseconds before the producer's database
    /// transaction becomes visible. A missing row is therefore retried briefly; an existing row
    /// that cannot be claimed means another delivery owns it (or it is already terminal) and is a
    /// true idempotent no-op.
    /// </summary>
    private async Task<JobClaimOutcome> TryClaimAfterAdmissionCommitAsync(
        Guid jobId,
        Guid processingToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 6;
        const int baseDelayMilliseconds = 20;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (await jobRepository.TryClaimAsync(
                    jobId,
                    processingToken,
                    leaseDuration,
                    cancellationToken))
                return JobClaimOutcome.Claimed;

            var visibleJob = await jobRepository.FindByJobIdAsReadOnlyAsync(jobId, cancellationToken);
            if (visibleJob is not null)
            {
                return visibleJob.IsActive
                    ? JobClaimOutcome.RetryRequired
                    : JobClaimOutcome.AlreadyTerminal;
            }

            if (attempt == maxAttempts - 1)
                return JobClaimOutcome.RetryRequired;

            var delay = Math.Min(baseDelayMilliseconds << attempt, 250);
            await Task.Delay(delay, cancellationToken);
        }

        return JobClaimOutcome.RetryRequired;
    }

    private enum JobClaimOutcome
    {
        Claimed,
        AlreadyTerminal,
        RetryRequired
    }

    private static Guid GetRequiredProcessingToken(Guid jobId, Guid? processingToken)
        => processingToken is { } token && token != Guid.Empty
            ? token
            : throw new InvalidOperationException(
                $"Transition job '{jobId}' has no processing token after a successful claim.");

    private static InvalidOperationException CreateDispatcherRetryException(Guid jobId, string reason)
        => new(
            jobId == Guid.Empty
                ? $"Legacy transition job requires dispatcher retry because {reason}."
                : $"Transition job '{jobId}' requires dispatcher retry because {reason}.");

    private static bool TryReadCanonicalPayload(
        InstanceJob job,
        TransitionJobPayload delivery,
        out TransitionJobPayload canonical,
        out string error)
    {
        canonical = null!;

        if (job.JobType != JobType.AsyncTransition)
        {
            error = $"Durable job type '{job.JobType}' is not an asynchronous transition.";
            return false;
        }

        if (!string.Equals(job.FlowName, delivery.Workflow, StringComparison.Ordinal))
        {
            error = "Delivery workflow does not match the durable job schema identity.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(job.Payload))
        {
            error = "Durable transition payload is missing.";
            return false;
        }

        try
        {
            canonical = JsonSerializer.Deserialize<TransitionJobPayload>(
                            job.Payload,
                            JsonSerializerOptions.Web)
                        ?? throw new JsonException("The durable payload deserialized to null.");
        }
        catch (JsonException exception)
        {
            error = $"Durable transition payload is not valid JSON: {exception.Message}";
            return false;
        }
        catch (NotSupportedException exception)
        {
            error = $"Durable transition payload cannot be deserialized: {exception.Message}";
            return false;
        }

        if (canonical.JobId != job.JobId || canonical.JobId != delivery.JobId)
        {
            error = "Durable payload JobId does not match the claimed job.";
            return false;
        }

        if (canonical.InstanceId != job.InstanceId
            || !string.Equals(canonical.JobName, job.JobName, StringComparison.Ordinal)
            || !string.Equals(canonical.Domain, job.Domain, StringComparison.Ordinal)
            || !string.Equals(canonical.Workflow, job.FlowName, StringComparison.Ordinal)
            || !string.Equals(canonical.TransitionKey, job.TransitionKey, StringComparison.Ordinal))
        {
            error = "Durable payload identity does not match the claimed InstanceJob row.";
            return false;
        }

        if (canonical.AdmissionToken != job.AdmissionToken
            || canonical.ChainToken != job.AdmissionToken
            || canonical.AdmittedRevision != job.AdmittedRevision)
        {
            error = "Durable payload admission metadata does not match the claimed InstanceJob row.";
            return false;
        }

        if (!Enum.IsDefined(canonical.ExecutionActor)
            || !Enum.IsDefined(canonical.TriggerType)
            || canonical.ChainDepth < 0
            || canonical.Headers is null
            || canonical.RouteValues is null)
        {
            error = "Durable transition payload contains invalid execution metadata.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(canonical.JobName)
            || string.IsNullOrWhiteSpace(canonical.Domain)
            || string.IsNullOrWhiteSpace(canonical.Workflow)
            || string.IsNullOrWhiteSpace(canonical.Version)
            || string.IsNullOrWhiteSpace(canonical.TransitionKey))
        {
            error = "Durable transition payload is missing required identity fields.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static TransitionJobPayload CreateRecoveryPayload(InstanceJob job)
        => new()
        {
            JobId = job.JobId,
            JobName = job.JobName,
            InstanceId = job.InstanceId,
            TransitionKey = job.TransitionKey ?? string.Empty,
            Domain = job.Domain,
            Workflow = job.FlowName,
            Version = string.Empty,
            ExecutionActor = ExecutionActor.System
        };

    /// <summary>
    /// Executes the transition pipeline, retrying transient instance-lock conflicts with
    /// bounded exponential backoff (100→200→400→800ms with defaults). The race window is
    /// milliseconds wide — the Dapr job fires ~5ms after scheduling while another holder
    /// (enqueue accept lock, finishing auto-chain) may still hold the execution lock.
    /// Delays run on the linked token so the job's execution budget still applies.
    /// </summary>
    private async Task<Result<TransitionOutput>> ExecuteWithLockConflictRetryAsync(
        WorkflowExecutionContext context,
        TransitionJobPayload args,
        CancellationToken cancellationToken)
    {
        var retryOptions = executionOptions.Value.LockConflictRetry;
        var maxAttempts = Math.Max(1, retryOptions.MaxAttempts);
        var baseDelayMs = Math.Max(0, retryOptions.BaseDelayMilliseconds);

        var result = await workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);

        for (var attempt = 1; attempt < maxAttempts && !result.IsSuccess && IsLockConflict(result.Error); attempt++)
        {
            // Guard misconfigured options: cap the shift and compute in long so the delay
            // can never go negative (Task.Delay would throw ArgumentOutOfRangeException).
            var shift = Math.Min(attempt - 1, 30);
            var delayMs = (int)Math.Min((long)baseDelayMs << shift, int.MaxValue);

            logger.TransitionJobLockConflictRetry(
                args.JobName, args.InstanceId, attempt, maxAttempts, delayMs);

            await Task.Delay(delayMs, cancellationToken);
            result = await workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Identifies a transient instance-lock conflict. Within the job's sync pipeline path,
    /// <see cref="WorkflowErrorCodes.ConflictWorkflow"/> is produced only by
    /// <c>WorkflowErrors.InstanceLockConflict</c> (pipeline entry and cancel preflight),
    /// both of which are retry-safe contention signals.
    /// </summary>
    private static bool IsLockConflict(Error error)
        => error.Code == WorkflowErrorCodes.ConflictWorkflow;
}
