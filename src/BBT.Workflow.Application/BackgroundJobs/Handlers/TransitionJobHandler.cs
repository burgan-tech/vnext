using System.Diagnostics;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Aether.MultiSchema;
using BBT.Aether.Tracing;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Handles asynchronous transition background jobs.
/// This handler processes workflow transition requests that were submitted with Sync=true.
/// </summary>
public sealed class TransitionJobHandler(
    IInstanceJobRepository jobRepository,
    IWorkflowExecutionService workflowExecutionService,
    ICurrentSchema currentSchema,
    IJobTimeoutRecoveryService recoveryService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    IHostApplicationLifetime hostLifetime,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<TransitionJobHandler> logger) : IBackgroundJobHandler<TransitionJobPayload>
{
    public const string HandlerName = "flow.transition";

    public async Task HandleAsync(TransitionJobPayload args, CancellationToken cancellationToken)
    {
        // Restore trace context from the original request for distributed tracing correlation
        using var activity = BackgroundJobActivityHelper.StartActivityContinuingTrace("TransitionJob.Execute", args);
        // The Dapr scheduler callback is a fresh HTTP request, so the client's X-Request-Id is not
        // ambient here — restore it from the captured request headers so log scopes and downstream
        // calls (Execution invoke, cross-domain) keep correlating to the originating request.
        var requestId = args.Headers.GetValueOrDefault(TelemetryConstants.HeaderNames.RequestId.ToLowerInvariant());
        using var correlationScope = string.IsNullOrEmpty(requestId) ? null : correlationIdProvider.Change(requestId);
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
                       [TelemetryConstants.TagNames.JobName] = args.JobName,
                       [TelemetryConstants.TagNames.RequestId] = requestId ?? "N/A"
                   }))
            {
                var timeoutSeconds = executionOptions.Value.TransitionJobTimeoutSeconds;

                // Separate execution budget CTS from the incoming Dapr/host cancellation token.
                // This lets us distinguish: own timeout vs Dapr HTTP timeout vs host shutdown.
                using var executionCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, executionCts.Token);

                bool needsRecovery = false;
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

                    // Every job this handler consumes was created by an accept that already
                    // reserved the instance (async accept) or by a chain continuation of a
                    // Busy instance — the pipeline must not re-run the Busy admission check.
                    context.IsPreReserved = true;

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
                catch (OperationCanceledException) when (executionCts.IsCancellationRequested)
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
                    // Host shutdown (SIGTERM) — recovery not feasible, host is going down
                    activity?.SetStatus(ActivityStatusCode.Error, "Job cancelled (host shutdown)");
                    logger.JobCancelled(args.JobName, args.TransitionKey, args.InstanceId);
                }
                catch (Exception e)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, e.Message);
                    activity?.AddTag("error.type", e.GetType().Name);
                    logger.JobFailed(e, args.JobName, args.InstanceId);
                }
                finally
                {
                    if (needsRecovery)
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

                    await jobRepository.MarkAsProcessedAsync(args.InstanceId, args.JobName, CancellationToken.None);
                }
            }
        }
    }

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
