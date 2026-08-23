using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Enqueues transition jobs on the scheduler, with a short bounded retry for transient failures.
/// <para>
/// The transactional-outbox fallback this gateway used to route to is gone (see
/// <see cref="ITransitionEnqueueGateway"/>), which changes the failure contract rather than removing
/// it: a failed schedule is now REPORTED instead of silently deferred to the Inbox relay. Callers
/// must propagate it — the durable <c>InstanceJob</c> intent is already committed by then, so a
/// swallowed failure means an intent nothing ever arms and an instance parked in Busy with no owner.
/// </para>
/// <para>
/// The retry exists because the scheduler is an out-of-process dependency reached over the Dapr
/// sidecar, where a connection reset or a sidecar restart is a normal, self-clearing event. It is
/// intentionally NOT configurable: the budget is sub-second by construction, and the accept path
/// calls this while it still holds the instance status lock (only recording the job — the scheduler
/// round-trip is deferred to the caller), so a tunable that could be raised to seconds would be a
/// way to hold that lock far longer than the milliseconds it is designed for.
/// </para>
/// </summary>
public sealed class TransitionEnqueueGateway(
    ITransitionJobEnqueuer jobEnqueuer,
    ILogger<TransitionEnqueueGateway> logger) : ITransitionEnqueueGateway
{
    /// <summary>Attempts, first try included. Three keeps the worst case under 200ms of delay.</summary>
    private const int MaxAttempts = 3;

    /// <summary>Delay before the first retry; doubles per attempt (50ms, 100ms).</summary>
    private const int BaseDelayMilliseconds = 50;

    /// <inheritdoc />
    public async Task<Result<IBackgroundJobArmHandle?>> EnqueueAsync(
        TransitionJobPayload payload,
        Guid jobId,
        bool deferArming = false,
        CancellationToken cancellationToken = default)
    {
        Error? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var result = await TryEnqueueAsync(payload, jobId, deferArming, cancellationToken);
            if (result.IsSuccess)
            {
                logger.TransitionContinuationEnqueued(
                    payload.InstanceId, payload.TransitionKey, payload.JobName);
                return result;
            }

            lastError = result.Error;

            if (attempt == MaxAttempts)
                break;

            logger.TransitionEnqueueRetrying(
                payload.JobName, payload.InstanceId, payload.TransitionKey, attempt, MaxAttempts,
                result.Error.Message ?? result.Error.Code);

            // Doubling from a 50ms base. Cancellation is allowed to THROW rather than be folded
            // into a failed Result: a cancelled token means host shutdown or an abandoned request,
            // and TransitionJobHandler routes OperationCanceledException through recovery. Turning
            // it into a Result here would instead have EnqueueContinuationStrategy fault the
            // instance, converting a benign shutdown into a Faulted workflow.
            await Task.Delay(BaseDelayMilliseconds * (1 << (attempt - 1)), cancellationToken);
        }

        // Error is a struct, so lastError is a Nullable<Error>: the loop always assigns it before
        // breaking, hence the unconditional unwrap.
        var error = lastError!.Value;
        logger.TransitionEnqueueFailed(
            payload.JobName, payload.InstanceId, payload.TransitionKey, MaxAttempts,
            error.Message ?? error.Code);

        return Result<IBackgroundJobArmHandle?>.Fail(error);
    }

    /// <summary>
    /// One enqueue attempt. Wrapped in TryAsync because the scheduler is an external dependency;
    /// catching here is safe because the caller has already persisted the durable intent and now
    /// gets a Result it is required to act on.
    /// </summary>
    private Task<Result<IBackgroundJobArmHandle?>> TryEnqueueAsync(
        TransitionJobPayload payload,
        Guid jobId,
        bool deferArming,
        CancellationToken cancellationToken)
    {
        return ResultExtensions.TryAsync<IBackgroundJobArmHandle?>(
            async ct =>
            {
                if (deferArming)
                    return await jobEnqueuer.EnqueueWithDeferredArmAsync(payload, jobId, ct);

                await jobEnqueuer.EnqueueAsync(payload, jobId, ct);
                return null;
            },
            cancellationToken,
            ex => Error.Dependency(
                WorkflowErrorCodes.Dependency,
                $"Failed to enqueue transition job '{payload.JobName}': {ex.Message}",
                "Dapr"));
    }
}
