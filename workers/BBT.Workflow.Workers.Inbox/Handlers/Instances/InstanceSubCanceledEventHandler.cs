using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Workers.Inbox.Forwarding;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards canceled SubItem outcomes to the parent workflow's internal cancel endpoint.
/// </summary>
internal sealed class InstanceSubCanceledEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ILogger<InstanceSubCanceledEventHandler> logger) : IEventHandler<InstanceSubCanceledEvent>
{
    public async Task HandleAsync(
        CloudEventEnvelope<InstanceSubCanceledEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;
        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.LogDebug(
                "InstanceSubCanceledEvent ignored because event domain {EventDomain} does not match runtime domain {RuntimeDomain}",
                eventData.Domain,
                runtimeInfoProvider.Domain);
            return;
        }

        var scopeProps = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId
        };
        if (eventData.RootInstanceId.HasValue)
        {
            scopeProps[TelemetryConstants.TagNames.RootInstanceId] = eventData.RootInstanceId.Value;
            Activity.Current?.SetBaggage(
                TelemetryConstants.TagNames.RootInstanceId,
                eventData.RootInstanceId.Value.ToString());
        }

        using (logger.BeginScope(scopeProps))
        {
            logger.SubFlowEventReceived(
                eventData.SubInstanceId,
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            var body = new SubItemCanceledInput
            {
                InstanceId = eventData.InstanceId,
                SubInstanceId = eventData.SubInstanceId,
                Domain = eventData.Domain,
                Flow = eventData.Flow,
                Version = eventData.Version,
                CanceledState = eventData.CanceledState,
                CanceledAt = eventData.CanceledAt,
                RootInstanceId = eventData.RootInstanceId,
                Sync = false,
                Termination = new TerminationContext(
                    eventData.TerminationOrigin,
                    eventData.InitiatorInstanceId,
                    eventData.CascadeId)
            };
            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/sub/cancel";
            await forwarder.ForwardAsync(
                HttpMethod.Post,
                route,
                body,
                eventData.Domain,
                eventData.Flow,
                eventData.Version,
                eventData.InstanceId,
                cancellationToken);
        }
    }
}
