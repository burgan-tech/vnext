using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Workers.Inbox.Forwarding;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="ChildSubflowFaultRequestedEvent"/> to the Orchestration <c>child-fault</c>
/// internal endpoint via Dapr service invocation. Thin relay: Orchestration's
/// <c>IChildSubflowFaultService</c> loads + faults the child (idempotency lives server-side now).
/// </summary>
internal sealed class ChildSubflowFaultRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ILogger<ChildSubflowFaultRequestedEventHandler> logger) : IEventHandler<ChildSubflowFaultRequestedEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<ChildSubflowFaultRequestedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.ChildSubflowFaultIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.InstanceId,
                eventData.Flow);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
        }))
        {
            logger.ChildSubflowFaultRequestReceived(
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/child-fault?parentInstanceId={eventData.ParentInstanceId}";
            await forwarder.ForwardAsync(HttpMethod.Post, route, new { },
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
