using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Workers.Inbox.Forwarding;
using BBT.Aether.Tracing;
using BBT.Workflow.Workers.Inbox.Tracing;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="InstanceFaultedCleanupEvent"/> to the Orchestration <c>cancel-cleanup</c>
/// internal endpoint via Dapr service invocation. Thin relay: Orchestration cancels scheduled jobs.
/// </summary>
internal sealed class InstanceFaultedCleanupEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<InstanceFaultedCleanupEventHandler> logger) : IEventHandler<InstanceFaultedCleanupEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<InstanceFaultedCleanupEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.InstanceFaultedCleanupEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.InstanceId,
                eventData.Flow);
            return;
        }

        using var traceScope = EventTraceScope.Start("InstanceFaultedCleanup.Handle", eventData, correlationIdProvider);

        var scopeProps = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.RequestId] = eventData.CorrelationId ?? "N/A",
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
        };
        if (eventData.RootInstanceId.HasValue)
        {
            scopeProps[TelemetryConstants.TagNames.RootInstanceId] = eventData.RootInstanceId.Value;
            Activity.Current?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId,
                eventData.RootInstanceId.Value.ToString());
        }
        using (logger.BeginScope(scopeProps))
        {
            logger.InstanceFaultedCleanupEventReceived(eventData.InstanceId, eventData.Flow);

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/cancel-cleanup";
            await forwarder.ForwardAsync(HttpMethod.Post, route, new { },
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
