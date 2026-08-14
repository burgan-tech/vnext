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
/// Forwards <see cref="ChildSubflowCancelRequestedEvent"/> to the Orchestration <c>child-cancel</c>
/// internal endpoint via Dapr service invocation. Thin relay: Orchestration's
/// <c>IChildSubflowCancellationService</c> cancels the child subflow.
/// </summary>
internal sealed class ChildSubflowCancelRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ICorrelationIdProvider correlationIdProvider,
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

        using var traceScope = EventTraceScope.Start("ChildSubflowCancelRequested.Handle", eventData, correlationIdProvider);

        var scopeProps = new Dictionary<string, object>
        {
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
            logger.ChildSubflowCancelRequestReceived(
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/child-cancel";
            var body = new
            {
                eventData.Version,
                eventData.Termination
            };
            await forwarder.ForwardAsync(HttpMethod.Post, route, body,
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
