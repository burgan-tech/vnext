using BBT.Aether.Events;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Workers.Inbox.Forwarding;
using BBT.Aether.Tracing;
using BBT.Workflow.Workers.Inbox.Tracing;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="TransitionContinuationRequested"/> (published via the transactional outbox
/// when <c>UseOutboxContinuations</c> is ON) to the Orchestration <c>transitions/{key}/enqueue</c>
/// internal endpoint via Dapr service invocation. Thin relay: the Dapr <c>flow.transition</c> job
/// is enqueued in the Orchestration process, never in the Inbox. Idempotency is enforced downstream
/// by the active-<c>InstanceJob</c> guard and the chain token.
/// </summary>
internal sealed class TransitionContinuationRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ICorrelationIdProvider correlationIdProvider,
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

        using var traceScope = EventTraceScope.Start(
            "TransitionContinuationRequested.Handle", eventData, correlationIdProvider,
            EventTraceMode.ContinueTrace, envelope.Id);

        logger.TransitionContinuationReceived(
            eventData.InstanceId, eventData.TransitionKey, eventData.JobName);

        var route =
            $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/transitions/{eventData.TransitionKey}/enqueue";

        await forwarder.ForwardAsync(HttpMethod.Post, route, eventData,
            eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);

        logger.TransitionContinuationEnqueued(
            eventData.InstanceId, eventData.TransitionKey, eventData.JobName);
    }
}
