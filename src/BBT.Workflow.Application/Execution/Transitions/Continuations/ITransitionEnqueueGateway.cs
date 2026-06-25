using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Events;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Routes a transition enqueue request to either a direct Dapr job or the transactional
/// outbox, based on <c>WorkflowExecutionOptions.DirectEnqueueContinuations</c>.
/// <para>
/// The caller is responsible for the ambient unit of work and for persisting the durable
/// <c>InstanceJob</c> intent before calling this. The gateway only decides the delivery path
/// (direct vs outbox) — it never opens its own unit of work.
/// </para>
/// </summary>
public interface ITransitionEnqueueGateway
{
    /// <summary>
    /// Delivers a transition job to the background job scheduler.
    /// Direct path (ON): enqueues via <c>ITransitionJobEnqueuer</c>; on failure falls back to outbox.
    /// Outbox path (OFF): always publishes <paramref name="outboxEvent"/> through the transactional outbox.
    /// </summary>
    /// <param name="directPayload">Payload for the direct Dapr enqueue path.</param>
    /// <param name="outboxEvent">Event for the outbox path (also used as the fallback on direct-enqueue failure).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task EnqueueAsync(
        TransitionJobPayload directPayload,
        TransitionContinuationRequested outboxEvent,
        CancellationToken cancellationToken);
}
