using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the auto-chain continuation as a SEPARATE background job (transition-per-job).
/// Instead of running the next transition in-process, it persists a durable job intent
/// (<see cref="InstanceJob"/>) and publishes a <see cref="TransitionContinuationRequested"/>
/// event through the transactional outbox — both within the AMBIENT transition unit of work,
/// so "this transition committed" and "next transition enqueued" are atomic. Returns Ok(null)
/// to end the in-process loop; the Inbox handler enqueues the actual Dapr job for the next hop.
/// </summary>
/// <remarks>Draft (S5) — not compiled; verify ambient-UoW outbox semantics in CI.</remarks>
public sealed class EnqueueContinuationStrategy(
    IDistributedEventBus eventBus,
    IInstanceJobRepository jobRepository) : IContinuationStrategy
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

        var jobName = $"trans-{current.InstanceId}-{next.TransitionKey}";

        // Durable intent for the active-job guard / reaper — atomic with the transition commit
        // because we run inside the pipeline's ambient UoW (TransitionRunner).
        await jobRepository.InsertAsync(
            InstanceJob.Create(
                Guid.NewGuid(),
                jobName,
                Guid.NewGuid(),
                current.Domain,
                current.WorkflowKey,
                current.InstanceId),
            true,
            cancellationToken);

        var continuation = new TransitionContinuationRequested
        {
            InstanceId = current.InstanceId,
            Domain = current.Domain,
            Flow = current.WorkflowKey,
            Version = current.Workflow.Version,
            TransitionKey = next.TransitionKey,
            JobName = jobName,
            Data = null, // chained auto-transitions carry no new request payload
            Headers = current.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            RouteValues = current.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExecutionActor = ExecutionActor.System.ToString(),
            ChainToken = current.ChainToken, // propagate chain ownership (S6)
            ChainDepth = current.ChainDepth + 1
        };

        await eventBus.PublishAsync(continuation, subject: null, useOutbox: true, cancellationToken);

        // No in-process next context — a separate job resumes the chain.
        return Result<WorkflowExecutionContext?>.Ok(null);
    }
}
