using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the auto-chain continuation as a SEPARATE background job (transition-per-job).
/// Instead of running the next transition in-process, it persists a durable job intent
/// (<see cref="InstanceJob"/>) within the AMBIENT transition unit of work — so "this transition
/// committed" and "next transition tracked" are atomic — and then delegates the enqueue
/// decision to <see cref="ITransitionEnqueueGateway"/>.
/// <para>
/// How the next hop is enqueued is governed by <c>WorkflowExecutionOptions.DirectEnqueueContinuations</c>:
/// </para>
/// <list type="bullet">
/// <item>ON (default): the Dapr job is enqueued DIRECTLY (no outbox/inbox poll hop). If the direct
/// enqueue fails, the gateway falls back to publishing a <see cref="TransitionContinuationRequested"/>
/// event through the transactional outbox so durability is preserved.</item>
/// <item>OFF: a <see cref="TransitionContinuationRequested"/> event is always published through the
/// transactional outbox; the Inbox handler then performs the real Dapr enqueue.</item>
/// </list>
/// Returns Ok(null) to end the in-process loop; a separate job resumes the chain.
/// </summary>
public sealed class EnqueueContinuationStrategy(
    IInstanceJobRepository jobRepository,
    ITransitionEnqueueGateway enqueueGateway) : IContinuationStrategy
{
    /// <inheritdoc />
    public ContinuationMode Mode => ContinuationMode.Enqueue;

    /// <inheritdoc />
    public async Task<Result<WorkflowExecutionContext?>> DispatchAsync(
        TransitionExecutionContext current,
        CancellationToken cancellationToken)
    {
        var next = current.Directives.ConsumeNextTransition();
        if (next is null)
            return Result<WorkflowExecutionContext?>.Ok(null);

        // Single caller-generated id, reused for the durable InstanceJob.JobId, the underlying
        // BackgroundJobInfo.Id (direct + outbox paths) AND the job name's invocation segment.
        // Keeps the three in sync so cancellation-by-id works — no placeholder.
        var jobId = Guid.NewGuid();

        // Scope by the state the auto-transition fires from (the state just entered) so two
        // same-named continuations across different states never share a name, and by the job id so
        // successive iterations of a $self loop do not either: the scheduler entry is keyed by name
        // and deleted by name when a one-shot job completes, so the finishing iteration would
        // otherwise delete the next iteration's trigger and leave the instance Busy for good.
        var sourceStateKey = current.Target?.Key ?? current.Current?.Key ?? string.Empty;
        var jobName = JobName.ForAsyncTransition(
            current.InstanceId, sourceStateKey, next.TransitionKey, jobId);
        var jobNameValue = jobName.Value;

        // Durable intent for the active-job guard / reaper — atomic with the transition commit
        // because we run inside the pipeline's ambient UoW (TransitionRunner).
        await jobRepository.InsertAsync(
            InstanceJob.Create(
                jobId,
                jobName,
                jobId,
                current.Domain,
                current.WorkflowKey,
                current.InstanceId),
            true,
            cancellationToken);

        var activity = Activity.Current;

        // The lane anchor is the PARENT of the next hop's span; activity.Id is only its PREDECESSOR
        // and is linked. That split is what makes hop N+1 a sibling of hop N instead of its child.
        var laneAnchor = WorkflowTraceLane.Current;
        var laneParent = WorkflowTraceLane.ParentLane;
        // Computed once: the gateway may fall back from the direct payload to the outbox event, and
        // incrementing at both sites would hand out the same ordinal twice.
        var laneSeq = WorkflowTraceLane.NextSeq();

        // The activation episode travels with the chain: the hop that finally brings the instance
        // to rest measures from the ORIGINAL trigger, not from itself.
        var episode = WorkflowTraceLane.Episode;

        var directPayload = new TransitionJobPayload
        {
            JobName = jobNameValue,
            InstanceId = current.InstanceId,
            TransitionKey = next.TransitionKey,
            Domain = current.Domain,
            Workflow = current.WorkflowKey,
            Version = current.Workflow.Version,
            Data = null, // chained auto-transitions carry no new request payload
            Headers = current.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            RouteValues = current.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExecutionActor = ExecutionActor.System,
            CallerSync = false,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            TraceRoot = laneAnchor,
            ParentTraceRoot = laneParent,
            ChainDepth = current.ChainDepth + 1,
            LaneSeq = laneSeq,
            EpisodeStartedAt = episode?.StartedAt,
            EpisodeTrigger = episode?.Trigger,
            EpisodeTransitionKey = episode?.TransitionKey,
            CorrelationId = current.CorrelationId
        };

        var outboxEvent = new TransitionContinuationRequested
        {
            InstanceId = current.InstanceId,
            Domain = current.Domain,
            Flow = current.WorkflowKey,
            Version = current.Workflow.Version,
            TransitionKey = next.TransitionKey,
            JobName = jobNameValue,
            JobId = jobId,
            Data = null, // chained auto-transitions carry no new request payload
            Headers = current.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            RouteValues = current.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExecutionActor = ExecutionActor.System.ToString(),
            ChainDepth = current.ChainDepth + 1,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            TraceRoot = laneAnchor,
            ParentTraceRoot = laneParent,
            LaneSeq = laneSeq,
            EpisodeStartedAt = episode?.StartedAt,
            EpisodeTrigger = episode?.Trigger,
            EpisodeTransitionKey = episode?.TransitionKey,
            CorrelationId = current.CorrelationId
        };

        // Auto-chain runs in the pipeline's ambient UoW and holds no status lock, so Aether already
        // defers arming to that UoW's post-commit hook. Nothing to move out here.
        await enqueueGateway.EnqueueAsync(directPayload, outboxEvent, cancellationToken: cancellationToken);

        // The chain goes on in another job: the settlement must not treat this hop's "nothing left
        // to run in-process" as the activation episode's rest point.
        current.Directives.MarkContinuationEnqueued();

        // No in-process next context — a separate job resumes the chain.
        return Result<WorkflowExecutionContext?>.Ok(null);
    }
}
