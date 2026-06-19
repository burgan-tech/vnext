using BBT.Workflow.BackgroundJobs.Payloads;

namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Enqueues a <c>flow.transition</c> Dapr background job from a fully-built payload. Centralizes
/// the schedule (+5ms one-shot), failure policy, and metadata so every caller — the async
/// transition strategy, the auto-chain continuation strategy (direct mode), and the Orchestration
/// continuation-enqueue endpoint (outbox mode) — enqueues identically. The Inbox never calls this;
/// transition jobs are enqueued only in the Orchestration process.
/// </summary>
public interface ITransitionJobEnqueuer
{
    /// <summary>
    /// Enqueues the transition job. The caller owns any durable <c>InstanceJob</c> intent (written
    /// in its unit of work) and supplies <paramref name="jobId"/> — the same id is used as the
    /// underlying <c>BackgroundJobInfo.Id</c>, so the tracking <c>InstanceJob.JobId</c> stays in sync
    /// (no placeholder) and cancellation-by-id remains reliable.
    /// </summary>
    Task EnqueueAsync(TransitionJobPayload payload, Guid jobId, CancellationToken cancellationToken = default);
}
