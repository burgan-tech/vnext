using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Workers.Inbox.Forwarding;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="ChildSubflowCancelRequestedEvent"/> to the Orchestration <c>child-cancel</c>
/// internal endpoint via Dapr service invocation. Thin relay: Orchestration's
/// <c>IChildSubflowCancellationService</c> cancels the child subflow.
/// </summary>
internal sealed class ChildSubflowCancelRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ILogger<ChildSubflowCancelRequestedEventHandler> logger) : IEventHandler<ChildSubflowCancelRequestedEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<ChildSubflowCancelRequestedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.ChildSubflowCancelEventIgnoredDomainMismatch(
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
            logger.ChildSubflowCancelRequestReceived(
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            var version = string.IsNullOrEmpty(eventData.Version) ? string.Empty : $"?version={eventData.Version}";
            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/child-cancel{version}";
            await forwarder.ForwardAsync(HttpMethod.Post, route, new { },
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
