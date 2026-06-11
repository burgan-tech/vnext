using BBT.Aether.BackgroundJob;
using BBT.Aether.Events;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using Dapr.Jobs.Models;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Handles <see cref="TransitionContinuationRequested"/> by enqueuing the actual Dapr
/// transition job. The event was committed atomically with the durable job intent via the
/// transactional outbox, so this handler is the distributed, at-least-once delivery of the
/// external Dapr enqueue (closing the dual-write gap of a pre-commit enqueue).
/// </summary>
/// <remarks>
/// Idempotency: at-least-once delivery may invoke this more than once; the downstream
/// <c>InstanceJob</c> active-job guard (and, later, the chain token) prevents duplicate
/// transition execution. Draft (S4) — not compiled; verify Dapr enqueue + UoW semantics in CI.
/// </remarks>
internal sealed class TransitionContinuationRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IBackgroundJobService backgroundJobService,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<TransitionContinuationRequestedEventHandler> logger)
    : IEventHandler<TransitionContinuationRequested>
{
    public async Task HandleAsync(
        CloudEventEnvelope<TransitionContinuationRequested> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.TransitionContinuationIgnoredDomainMismatch(
                eventData.Domain, runtimeInfoProvider.Domain, eventData.InstanceId);
            return;
        }

        logger.TransitionContinuationReceived(
            eventData.InstanceId, eventData.TransitionKey, eventData.JobName);

        var actor = Enum.TryParse<ExecutionActor>(eventData.ExecutionActor, ignoreCase: true, out var parsed)
            ? parsed
            : ExecutionActor.System;

        var payload = new TransitionJobPayload
        {
            JobName = eventData.JobName,
            InstanceId = eventData.InstanceId,
            TransitionKey = eventData.TransitionKey,
            Domain = eventData.Domain,
            Workflow = eventData.Flow,
            Version = eventData.Version,
            Data = eventData.Data,
            InstanceKey = eventData.InstanceKey,
            Tags = eventData.Tags,
            Stage = eventData.Stage,
            Headers = eventData.Headers,
            RouteValues = eventData.RouteValues,
            ExecutionActor = actor,
            CallerSync = false,
            TraceParent = eventData.TraceParent,
            TraceState = eventData.TraceState,
            ChainToken = eventData.ChainToken
        };

        var fp = executionOptions.Value.FailurePolicy;
        var failurePolicy = JobScheduleFailurePolicy.Constant(
            TimeSpan.FromSeconds(fp.IntervalSeconds),
            (uint)fp.MaxRetries);

        var schedule = DaprJobSchedule.FromDateTime(DateTime.UtcNow.AddMilliseconds(5)).ExpressionValue;

        var metadata = new Dictionary<string, object>
        {
            ["domain"] = eventData.Domain,
            ["flowName"] = eventData.Flow,
            ["instanceId"] = eventData.InstanceId.ToString()
        };

        try
        {
            await backgroundJobService.EnqueueAsync(
                TransitionJobHandler.HandlerName,
                eventData.JobName,
                payload,
                schedule,
                metadata,
                failurePolicy,
                cancellationToken);

            logger.TransitionContinuationEnqueued(
                eventData.InstanceId, eventData.TransitionKey, eventData.JobName);
        }
        catch (Exception ex)
        {
            logger.TransitionContinuationEnqueueFailed(
                eventData.InstanceId, eventData.TransitionKey, eventData.JobName, ex.Message);
            throw; // let the Inbox retry (at-least-once)
        }
    }
}
