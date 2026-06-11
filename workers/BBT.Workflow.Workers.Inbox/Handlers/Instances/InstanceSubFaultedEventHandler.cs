using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Workers.Inbox.Forwarding;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards <see cref="InstanceSubFaultedEvent"/> to the Orchestration <c>sub/fault</c> internal
/// endpoint via Dapr service invocation. Thin relay: Orchestration's <c>ISubflowFaultService</c>
/// propagates the fault to the parent.
/// </summary>
internal sealed class InstanceSubFaultedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ILogger<InstanceSubFaultedEventHandler> logger) : IEventHandler<InstanceSubFaultedEvent>
{
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubFaultedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowFaultIgnoredDomainMismatch(
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
            logger.SubFlowFaultReceived(
                eventData.SubInstanceId,
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            var body = new SubFlowFaultedInput
            {
                SubInstanceId = eventData.SubInstanceId,
                InstanceId = eventData.InstanceId,
                Domain = eventData.Domain,
                Flow = eventData.Flow,
                Version = eventData.Version,
                FaultedState = eventData.FaultedState,
                FaultedStateType = eventData.FaultedStateType,
                FaultedStateSubType = eventData.FaultedStateSubType,
                InstanceData = eventData.InstanceData,
                FaultedAt = eventData.FaultedAt,
                SubFlowName = eventData.SubFlowName,
                IncidentMessage = eventData.IncidentMessage,
                IncidentErrorCode = eventData.IncidentErrorCode,
                IncidentErrorLayer = eventData.IncidentErrorLayer,
                IncidentStatusCode = eventData.IncidentStatusCode,
                IncidentTraceId = eventData.IncidentTraceId,
                IncidentTaskKey = eventData.IncidentTaskKey,
                IncidentTransition = eventData.IncidentTransition,
                IncidentState = eventData.IncidentState,
                IncidentBoundaryAction = eventData.IncidentBoundaryAction,
                IncidentBoundaryLevel = eventData.IncidentBoundaryLevel
            };

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/sub/fault";
            await forwarder.ForwardAsync(HttpMethod.Post, route, body,
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
