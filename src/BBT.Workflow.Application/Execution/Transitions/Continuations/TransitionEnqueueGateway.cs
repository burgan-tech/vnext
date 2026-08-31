using BBT.Aether.BackgroundJob;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Routes a transition enqueue request to the direct Dapr path or the transactional outbox.
/// <para>
/// When <c>DirectEnqueueContinuations</c> is ON (default), the job is submitted directly via
/// <see cref="ITransitionJobEnqueuer"/>. A failure falls back to the outbox so the continuation
/// is never lost. When OFF, the outbox path is always used (at-least-once via Inbox).
/// </para>
/// <para>
/// The caller owns the ambient unit of work and the durable <c>InstanceJob</c> insert. This
/// gateway is a pure routing concern — it never opens its own unit of work.
/// </para>
/// </summary>
public sealed class TransitionEnqueueGateway(
    ITransitionJobEnqueuer jobEnqueuer,
    IDistributedEventBus eventBus,
    IOptions<WorkflowExecutionOptions> options,
    ILogger<TransitionEnqueueGateway> logger) : ITransitionEnqueueGateway
{
    /// <inheritdoc />
    public async Task<TransitionEnqueueOutcome> EnqueueAsync(
        TransitionJobPayload directPayload,
        TransitionContinuationRequested outboxEvent,
        bool deferArming = false,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.DirectEnqueueContinuations)
        {
            var result = await TryEnqueueDirectlyAsync(
                directPayload, outboxEvent.JobId, deferArming, cancellationToken);
            if (result.IsSuccess)
            {
                logger.TransitionContinuationEnqueued(
                    outboxEvent.InstanceId, outboxEvent.TransitionKey, outboxEvent.JobName);
                return new TransitionEnqueueOutcome(TransitionEnqueuePath.Direct, result.Value);
            }

            logger.TransitionContinuationFellBackToOutbox(
                outboxEvent.InstanceId, outboxEvent.TransitionKey, outboxEvent.JobName,
                result.Error.Message ?? result.Error.Code);
        }

        await eventBus.PublishAsync(outboxEvent, subject: null, useOutbox: true, cancellationToken);
        return new TransitionEnqueueOutcome(TransitionEnqueuePath.Outbox);
    }

    /// <summary>
    /// Attempts to enqueue the job directly via <see cref="ITransitionJobEnqueuer"/>.
    /// Uses TryAsync because Dapr is an external dependency; failures are safe to catch here
    /// as the intent has already been persisted by the caller.
    /// </summary>
    private Task<Result<IBackgroundJobArmHandle?>> TryEnqueueDirectlyAsync(
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
