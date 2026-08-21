using BBT.Aether.BackgroundJob;
using BBT.Aether.Domain.Entities;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.BackgroundJobs;

/// <summary>
/// Default <see cref="ITransitionJobEnqueuer"/> over the Aether background-job service / Dapr Jobs.
/// Enqueues with <c>useAmbientUnitOfWork: true</c> so the durable job row joins the transition
/// pipeline's ambient unit of work (committing atomically with the transition) and the Dapr schedule
/// is deferred to post-commit — avoiding the nested-UoW / shared-DbContext collision
/// ("A second operation was started on this context instance").
/// </summary>
public sealed class TransitionJobEnqueuer(
    IBackgroundJobService backgroundJobService,
    IOptions<WorkflowExecutionOptions> executionOptions) : ITransitionJobEnqueuer
{
    /// <inheritdoc />
    public Task EnqueueAsync(TransitionJobPayload payload, Guid jobId, CancellationToken cancellationToken = default)
    {
        var a = BuildArgs(payload);
        return backgroundJobService.EnqueueAsync(
            TransitionJobHandler.HandlerName,
            payload.JobName,
            payload,
            a.Schedule,
            a.Metadata,
            a.FailurePolicy,
            // Arms the scheduler as part of the enqueue: inline with no ambient unit of work, or on
            // the ambient UoW's OnCompleted when there is one. Correct for callers that hold no lock.
            directly: true,
            jobId: jobId,
            kind: JobKind.OneShot,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IBackgroundJobArmHandle> EnqueueWithDeferredArmAsync(
        TransitionJobPayload payload, Guid jobId, CancellationToken cancellationToken = default)
    {
        var a = BuildArgs(payload);
        return backgroundJobService.EnqueueWithDeferredArmAsync(
            TransitionJobHandler.HandlerName,
            payload.JobName,
            payload,
            a.Schedule,
            a.Metadata,
            a.FailurePolicy,
            jobId: jobId,
            kind: JobKind.OneShot,
            cancellationToken);
    }

    /// <summary>
    /// The schedule, failure policy and metadata every transition job is enqueued with, so both entry
    /// points stay identical in everything except when the scheduler is called.
    /// </summary>
    private (string Schedule, Dictionary<string, object> Metadata, JobScheduleFailurePolicy FailurePolicy)
        BuildArgs(TransitionJobPayload payload)
    {
        var fp = executionOptions.Value.FailurePolicy;
        var failurePolicy = JobScheduleFailurePolicy.Constant(
            TimeSpan.FromSeconds(fp.IntervalSeconds),
            (uint)fp.MaxRetries);

        // Fire as soon as the scheduler will take it. The former 5 ms lead was not a correctness
        // guard: measured armings routinely complete well AFTER the due instant they requested
        // (p50 214 ms under load), and Dapr fires a past-due one-shot job immediately — 2167 jobs
        // observed, zero lost, zero redelivered. The lead only added latency to a path whose whole
        // budget is ~20 ms, so it is spent, not saved.
        //
        // Must stay an INSTANT. DaprJobSchedule.FromDuration(TimeSpan.Zero) renders as "@every ",
        // which Aether's DetectKind classifies as a cron expression — a malformed RECURRING
        // schedule, not an immediate one-shot.
        var schedule = DaprJobSchedule.FromDateTime(DateTime.UtcNow).ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = payload.Domain,
            ["flowName"] = payload.Workflow,
            ["instanceId"] = payload.InstanceId.ToString()
        };

        return (schedule, metadata, failurePolicy);
    }
}
