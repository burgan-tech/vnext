using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the auto-chain continuation as a SEPARATE background job (transition-per-job).
/// Instead of running the next transition in-process, it persists a durable job intent
/// (<see cref="InstanceJob"/>) within the AMBIENT transition unit of work — so "this transition
/// committed" and "next transition tracked" are atomic — and then enqueues the next hop.
/// <para>
/// How the next hop is enqueued is governed by <see cref="WorkflowExecutionOptions.DirectEnqueueContinuations"/>:
/// </para>
/// <list type="bullet">
/// <item>ON (default): the Dapr job is enqueued DIRECTLY (no outbox/inbox poll hop). If the direct
/// enqueue fails, the strategy falls back to publishing a <see cref="TransitionContinuationRequested"/>
/// event through the transactional outbox so durability is preserved.</item>
/// <item>OFF: a <see cref="TransitionContinuationRequested"/> event is always published through the
/// transactional outbox; the Inbox handler then performs the real Dapr enqueue.</item>
/// </list>
/// Returns Ok(null) to end the in-process loop; a separate job resumes the chain.
/// </summary>
public sealed class EnqueueContinuationStrategy(
    IDistributedEventBus eventBus,
    IInstanceJobRepository jobRepository,
    ITransitionJobEnqueuer jobEnqueuer,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<EnqueueContinuationStrategy> logger) : IContinuationStrategy
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

        var jobName = JobName.ForAsyncTransition(current.InstanceId, next.TransitionKey);
        var jobNameValue = jobName.Value;

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

        // Default: enqueue the Dapr job directly (no outbox/inbox poll hop). On failure, fall back
        // to the transactional outbox so the continuation is never lost.
        if (executionOptions.Value.DirectEnqueueContinuations)
        {
            var enqueueResult = await EnqueueDirectlyAsync(current, next.TransitionKey, jobNameValue, cancellationToken);
            if (enqueueResult.IsSuccess)
            {
                logger.TransitionContinuationEnqueued(current.InstanceId, next.TransitionKey, jobNameValue);
                return Result<WorkflowExecutionContext?>.Ok(null);
            }

            logger.TransitionContinuationFellBackToOutbox(
                current.InstanceId, next.TransitionKey, jobNameValue, enqueueResult.Error.Message);
        }

        await PublishViaOutboxAsync(current, next.TransitionKey, jobNameValue, cancellationToken);

        // No in-process next context — a separate job resumes the chain.
        return Result<WorkflowExecutionContext?>.Ok(null);
    }

    /// <summary>
    /// Enqueues the next-hop Dapr job directly via <see cref="ITransitionJobEnqueuer"/>.
    /// Wrapped in <see cref="ResultExtensions.TryAsync{T}"/> because Dapr is an external service.
    /// </summary>
    private Task<Result<bool>> EnqueueDirectlyAsync(
        TransitionExecutionContext current,
        string transitionKey,
        string jobName,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;
        var payload = new TransitionJobPayload
        {
            JobName = jobName,
            InstanceId = current.InstanceId,
            TransitionKey = transitionKey,
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
            ChainToken = current.ChainToken // propagate chain ownership (S6)
        };

        return ResultExtensions.TryAsync(
            async ct =>
            {
                await jobEnqueuer.EnqueueAsync(payload, ct);
                return true;
            },
            cancellationToken,
            ex => Error.Dependency(
                WorkflowErrorCodes.Dependency,
                $"Failed to enqueue transition job '{jobName}': {ex.Message}",
                "Dapr"));
    }

    /// <summary>
    /// Publishes a <see cref="TransitionContinuationRequested"/> event through the transactional
    /// outbox within the ambient transition UoW. Used as the legacy path and the direct-enqueue
    /// fallback; the Inbox handler performs the real Dapr enqueue (at-least-once).
    /// </summary>
    private Task PublishViaOutboxAsync(
        TransitionExecutionContext current,
        string transitionKey,
        string jobName,
        CancellationToken cancellationToken)
    {
        var continuation = new TransitionContinuationRequested
        {
            InstanceId = current.InstanceId,
            Domain = current.Domain,
            Flow = current.WorkflowKey,
            Version = current.Workflow.Version,
            TransitionKey = transitionKey,
            JobName = jobName,
            Data = null, // chained auto-transitions carry no new request payload
            Headers = current.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            RouteValues = current.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExecutionActor = ExecutionActor.System.ToString(),
            ChainToken = current.ChainToken, // propagate chain ownership (S6)
            ChainDepth = current.ChainDepth + 1
        };

        return eventBus.PublishAsync(continuation, subject: null, useOutbox: true, cancellationToken);
    }
}
