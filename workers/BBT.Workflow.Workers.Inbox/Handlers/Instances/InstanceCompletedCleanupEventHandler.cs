using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Workers.Inbox.Forwarding;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="InstanceCompletedCleanupEvent"/> to the Orchestration <c>cancel-cleanup</c>
/// internal endpoint via Dapr service invocation. Thin relay: Orchestration cancels scheduled jobs.
/// </summary>
internal sealed class InstanceCompletedCleanupEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ILogger<InstanceCompletedCleanupEventHandler> logger) : IEventHandler<InstanceCompletedCleanupEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<InstanceCompletedCleanupEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.InstanceCompletedCleanupEventIgnoredDomainMismatch(
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
            logger.InstanceCompletedCleanupEventReceived(eventData.InstanceId, eventData.Flow);

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/cancel-cleanup";
            await forwarder.ForwardAsync(HttpMethod.Post, route, new { },
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
