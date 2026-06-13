using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Workers.Inbox.Forwarding;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="InstanceSubCompletedEvent"/> to the Orchestration <c>complete</c> internal
/// endpoint via Dapr service invocation. Thin relay: no domain processing here — Orchestration's
/// <c>ISubflowCompletionService</c> runs the completion. Domain guard stays local to avoid
/// forwarding cross-domain events.
/// </summary>
internal sealed class InstanceSubCompletedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ILogger<InstanceSubCompletedEventHandler> logger) : IEventHandler<InstanceSubCompletedEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubCompletedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.SubInstanceId,
                eventData.InstanceId);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId,
        }))
        {
            logger.SubFlowEventReceived(
                eventData.SubInstanceId,
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            var body = new FlowCompletedInput
            {
                SubInstanceId = eventData.SubInstanceId,
                InstanceId = eventData.InstanceId,
                Domain = eventData.Domain,
                Flow = eventData.Flow,
                Version = eventData.Version,
                CompletedState = eventData.CompletedState,
                InstanceData = eventData.InstanceData,
                CompletedAt = eventData.CompletedAt,
                Duration = eventData.Duration
            };

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/complete";
            await forwarder.ForwardAsync(HttpMethod.Post, route, body,
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
