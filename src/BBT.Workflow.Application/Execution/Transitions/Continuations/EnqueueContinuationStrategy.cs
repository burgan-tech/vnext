using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Execution.Continuations;

/// <summary>
/// Realizes the auto-chain continuation as a SEPARATE background job (transition-per-job).
/// Instead of running the next transition in-process, it persists a durable job intent
/// (<see cref="InstanceJob"/>) together with its scheduler/outbox intent in a short transactional
/// handoff unit of work, then delegates the delivery decision to
/// <see cref="ITransitionEnqueueGateway"/>.
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
    ITransitionEnqueueGateway enqueueGateway,
    IUnitOfWorkManager unitOfWorkManager) : IContinuationStrategy
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

        // Scope by the state the auto-transition fires from (the state just entered) so two
        // same-named continuations across different states do not dedup into one Dapr job.
        var sourceStateKey = current.Target?.Key ?? current.Current?.Key ?? string.Empty;
        var jobName = JobName.ForAsyncTransition(current.InstanceId, sourceStateKey, next.TransitionKey);
        var jobNameValue = jobName.Value;

        // Single caller-generated id, reused for the durable InstanceJob.JobId AND the underlying
        // BackgroundJobInfo.Id (direct + outbox paths). Keeps the two in sync so cancellation-by-id
        // works — no placeholder.
        var jobId = Guid.NewGuid();
        var chainToken = current.ChainToken ?? current.Instance.ChainToken
            ?? throw new InvalidOperationException(
                $"Cannot enqueue transition '{next.TransitionKey}' without a chain ownership token.");
        if (!current.Instance.MatchesChain(chainToken))
        {
            throw new InvalidOperationException(
                $"Cannot enqueue transition '{next.TransitionKey}' because its chain token does not own instance '{current.InstanceId}'.");
        }

        var activity = Activity.Current;

        var directPayload = new TransitionJobPayload
        {
            JobId = jobId,
            JobName = jobNameValue,
            InstanceId = current.InstanceId,
            TransitionKey = next.TransitionKey,
            Domain = current.Domain,
            Workflow = current.WorkflowKey,
            Version = current.Workflow.Version,
            Data = null, // chained auto-transitions carry no new request payload
            Headers = DurableHeaderFilter.ForPersistence(current.Headers),
            RouteValues = current.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExecutionActor = ExecutionActor.System,
            CallerSync = false,
            TraceParent = activity?.Id,
            TraceState = activity?.TraceStateString,
            ChainToken = chainToken, // propagate chain ownership (S6)
            AdmissionToken = chainToken,
            ChainDepth = current.ChainDepth + 1,
            TriggerType = TriggerType.Automatic,
            IsReentry = true,
            IsErrorBoundaryTransition = string.Equals(
                next.Reason,
                TransitionRequestReasons.ErrorBoundary,
                StringComparison.OrdinalIgnoreCase)
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
            Headers = DurableHeaderFilter.ForPersistence(current.Headers),
            RouteValues = current.RouteValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            ExecutionActor = ExecutionActor.System.ToString(),
            ChainToken = chainToken, // propagate chain ownership (S6)
            ChainDepth = current.ChainDepth + 1,
            TriggerType = (int)TriggerType.Automatic,
            IsReentry = true,
            IsErrorBoundaryTransition = string.Equals(
                next.Reason,
                TransitionRequestReasons.ErrorBoundary,
                StringComparison.OrdinalIgnoreCase)
        };

        // Checkpointed task execution intentionally does not run inside one outer transaction.
        // Use a short, dedicated transaction for the handoff itself so canonical InstanceJob and
        // Aether scheduler/outbox intent either both commit or both roll back.
        await using var handoffUow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        // Persist the exact payload the handler is allowed to execute. The delivery body is only
        // a locator; canonical payload and delivery intent share the handoff transaction.
        var job = InstanceJob.CreateTransitionAdmission(
            jobId,
            jobName,
            jobId,
            current.Domain,
            current.WorkflowKey,
            current.InstanceId,
            JsonSerializer.Serialize(directPayload, JsonSerializerOptions.Web),
            chainToken,
            admittedRevision: null);
        await jobRepository.InsertAsync(job, false, cancellationToken);

        await enqueueGateway.EnqueueAsync(directPayload, outboxEvent, cancellationToken);
        job.MarkAsScheduled();
        await handoffUow.CommitAsync(cancellationToken);

        // No in-process next context — a separate job resumes the chain.
        return Result<WorkflowExecutionContext?>.Ok(null);
    }
}
