using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the chained continuation as a SEPARATE scheduler job — the
/// <c>AutoTransitionMode.Scheduled</c> path. Instead of running the next transition in-process, it
/// persists a durable job intent (<see cref="InstanceJob"/>) within the AMBIENT transition unit of
/// work — so "this transition committed" and "next transition tracked" are atomic — and then hands
/// the delivery to <see cref="ITransitionEnqueueGateway"/>.
/// <para>
/// Returns Ok(null) to end the in-process loop; a separate job resumes the chain.
/// </para>
/// <para>
/// A failed enqueue is PROPAGATED, never swallowed. The intent has already been written into the
/// ambient unit of work, and there is no outbox fallback behind the gateway any more, so a swallowed
/// failure would commit an intent nothing ever arms and strand the instance in Busy with no owner.
/// Failing here routes the pipeline into <c>MarkInstanceFaultedAsync</c> instead: visible, and
/// retryable.
/// </para>
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
        // BackgroundJobInfo.Id AND the job name's invocation segment. Keeps the three in sync so
        // cancellation-by-id works — no placeholder.
        var jobId = Guid.NewGuid();

        // Scope by the state the auto-transition fires from (the state just entered) so two
        // same-named continuations across different states never share a name, and by the job id so
        // successive iterations of a $self loop do not either: the scheduler entry is keyed by name
        // and deleted by name when a one-shot job completes, so the finishing iteration would
        // otherwise delete the next iteration's trigger and leave the instance Busy for good.
        var sourceStateKey = current.Target?.Key ?? current.Current?.Key ?? string.Empty;
        var jobName = JobName.ForAsyncTransition(
            current.InstanceId, sourceStateKey, next.TransitionKey, jobId);

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
        var payload = new TransitionJobPayload
        {
            JobName = jobName.Value,
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
            TraceRoot = WorkflowTraceLane.Current,
            ParentTraceRoot = WorkflowTraceLane.ParentLane,
            ChainDepth = current.ChainDepth + 1,
            LaneSeq = WorkflowTraceLane.NextSeq(),
            CorrelationId = current.CorrelationId
        };

        // Auto-chain runs in the pipeline's ambient UoW and holds no status lock, so Aether already
        // defers arming to that UoW's post-commit hook. Nothing to move out here.
        var enqueueResult = await enqueueGateway.EnqueueAsync(
            payload, jobId, cancellationToken: cancellationToken);
        if (!enqueueResult.IsSuccess)
            return Result<WorkflowExecutionContext?>.Fail(enqueueResult.Error);

        // No in-process next context — a separate job resumes the chain.
        return Result<WorkflowExecutionContext?>.Ok(null);
    }
}
