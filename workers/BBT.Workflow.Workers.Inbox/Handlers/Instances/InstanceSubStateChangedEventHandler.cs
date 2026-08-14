using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Workers.Inbox.Forwarding;
using BBT.Aether.Tracing;
using BBT.Workflow.Workers.Inbox.Tracing;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="InstanceSubStateChangedEvent"/> to the Orchestration <c>sub/state</c>
/// internal endpoint via Dapr service invocation. Thin relay: Orchestration's
/// <c>ISubflowStateService</c> updates the parent's EffectiveState.
/// </summary>
internal sealed class InstanceSubStateChangedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<InstanceSubStateChangedEventHandler> logger) : IEventHandler<InstanceSubStateChangedEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubStateChangedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.SubInstanceId,
                eventData.ParentInstanceId);
            return;
        }

        using var traceScope = EventTraceScope.Start("InstanceSubStateChanged.Handle", eventData, correlationIdProvider);

        var scopeProps = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.RequestId] = eventData.CorrelationId ?? "N/A",
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.ParentInstanceId,
            [TelemetryConstants.TagNames.ParentInstanceId] = eventData.ParentInstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId,
        };
        if (eventData.RootInstanceId.HasValue)
        {
            scopeProps[TelemetryConstants.TagNames.RootInstanceId] = eventData.RootInstanceId.Value;
            Activity.Current?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId,
                eventData.RootInstanceId.Value.ToString());
        }
        using (logger.BeginScope(scopeProps))
        {
            logger.SubFlowStateChangedEventReceived(
                eventData.SubInstanceId,
                eventData.ParentInstanceId,
                eventData.NewState);

            var body = new SubFlowStateChangedInput
            {
                ParentInstanceId = eventData.ParentInstanceId,
                SubInstanceId = eventData.SubInstanceId,
                Domain = eventData.Domain,
                Flow = eventData.Flow,
                Version = eventData.Version,
                NewState = eventData.NewState,
                PreviousState = eventData.PreviousState,
                NewStateType = (StateType)eventData.NewStateType,
                NewStateSubType = (StateSubType)eventData.NewStateSubType,
                ChangedAt = eventData.ChangedAt
            };

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.ParentInstanceId}/sub/state";
            await forwarder.ForwardAsync(HttpMethod.Post, route, body,
                eventData.Domain, eventData.Flow, eventData.Version, eventData.ParentInstanceId, cancellationToken);
        }
    }
}
