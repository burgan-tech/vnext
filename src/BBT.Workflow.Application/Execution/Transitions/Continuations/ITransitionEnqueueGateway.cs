using BBT.Aether.BackgroundJob;
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
    /// <param name="deferArming">
    /// When true, the direct path records the job but does NOT call the scheduler; the caller must arm
    /// it via the returned <c>ArmHandle</c> afterwards. Used by the accept path so the external call
    /// happens outside the instance status lock. Ignored on the outbox path, where the relay arms.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>Which path delivered the job, plus the arm handle when one is owed.</returns>
    Task<TransitionEnqueueOutcome> EnqueueAsync(
        TransitionJobPayload directPayload,
        TransitionContinuationRequested outboxEvent,
        bool deferArming = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of an enqueue: which path delivered it, and — on the deferred direct path — the handle
/// that still owes the scheduler a call.
/// </summary>
/// <param name="Path">The delivery path taken.</param>
/// <param name="ArmHandle">
/// Set only when the direct path recorded the job with arming deferred. Null on the outbox path (the
/// relay arms its own delivery) and when the direct path armed inline.
/// </param>
public readonly record struct TransitionEnqueueOutcome(
    TransitionEnqueuePath Path,
    IBackgroundJobArmHandle? ArmHandle = null);

/// <summary>
/// Which delivery path an enqueue took.
/// </summary>
public enum TransitionEnqueuePath
{
    /// <summary>Recorded on the direct path. With <c>deferArming</c>, an arm is still owed.</summary>
    Direct = 0,

    /// <summary>Published to the transactional outbox; the Inbox relay owns delivery and arming.</summary>
    Outbox = 1
}
