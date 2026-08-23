using BBT.Aether.BackgroundJob;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Delivers a transition job to the scheduler.
/// <para>
/// There is one delivery path: the scheduler. The transactional-outbox alternative — publish a
/// continuation event, let the Outbox worker publish it, the Inbox relay forward it, and
/// Orchestration finally enqueue the job — is gone. It bought durability with three extra hops of
/// latency on a path whose whole purpose is to be fast, and it was reachable only as a fallback,
/// which meant a rarely-exercised second code path for the same outcome.
/// </para>
/// <para>
/// The caller owns the ambient unit of work and the durable <c>InstanceJob</c> intent. This gateway
/// never opens its own unit of work. Because the outbox no longer backstops a failed schedule, the
/// gateway returns a <see cref="Result{T}"/> the caller MUST honour: swallowing it would leave a
/// committed intent that nothing ever arms, i.e. an instance parked in Busy with no owner.
/// </para>
/// </summary>
public interface ITransitionEnqueueGateway
{
    /// <summary>
    /// Enqueues a transition job, retrying briefly on transient scheduler failures.
    /// </summary>
    /// <param name="payload">The job payload.</param>
    /// <param name="jobId">
    /// The caller-generated id, shared with the durable <c>InstanceJob.JobId</c> and the job name's
    /// invocation segment so cancellation-by-id works.
    /// </param>
    /// <param name="deferArming">
    /// When true the job is recorded but the scheduler is NOT called; the caller must arm it through
    /// the returned handle. Used by the accept path so the external call happens outside the
    /// instance status lock.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// Ok with the arm handle when arming was deferred, Ok(null) when the job was armed inline, or
    /// Fail when the scheduler could not be reached within the retry budget.
    /// </returns>
    Task<Result<IBackgroundJobArmHandle?>> EnqueueAsync(
        TransitionJobPayload payload,
        Guid jobId,
        bool deferArming = false,
        CancellationToken cancellationToken = default);
}
