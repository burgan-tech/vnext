using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Workers.Inbox.Forwarding;
using BBT.Aether.Tracing;
using BBT.Workflow.Workers.Inbox.Tracing;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Forwards canceled SubItem outcomes to the parent workflow's internal cancel endpoint.
/// </summary>
internal sealed class InstanceSubCanceledEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ICorrelationIdProvider correlationIdProvider,
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

        using var traceScope = EventTraceScope.Start(
            "InstanceSubCanceled.Handle", eventData, correlationIdProvider,
            EventTraceMode.LinkedDelivery, envelope.Id, eventData.RearmAttempt);

        // This delivery is the durable BACKUP of the post-commit terminal relay: in the normal case the
        // relay already settled the parent and the settlement path answers AlreadySettled via the
        // pre-lock probe. Dashboards separate primary vs backup deliveries on this tag.
        Activity.Current?.SetTag(TelemetryConstants.TagNames.DeliveryRole, "backup");

        var scopeProps = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.RootInstanceId] = eventData.RootInstanceId?.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.ParentInstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId,
            [TelemetryConstants.TagNames.SubItemType] = eventData.SubItemType.ToString(),
            [TelemetryConstants.TagNames.SubItemOutcome] = "Canceled",
            [TelemetryConstants.TagNames.TerminationOrigin] = eventData.TerminationOrigin.ToString(),
            [TelemetryConstants.TagNames.TerminationInitiator] = eventData.InitiatorInstanceId.ToString(),
            [TelemetryConstants.TagNames.TerminationCascadeId] = eventData.CascadeId.ToString()
        };
        if (eventData.RootInstanceId.HasValue)
        {
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
                    eventData.CascadeId),
                // Relay the lane so the parent resume on the receiving side lands at the parent
                // instance's level rather than nesting under the relay endpoint.
                TraceRoot = eventData.TraceRoot,
                ParentTraceRoot = eventData.ParentTraceRoot,
                EpisodeStartedAt = eventData.EpisodeStartedAt,
                EpisodeTrigger = eventData.EpisodeTrigger,
                EpisodeTransitionKey = eventData.EpisodeTransitionKey,
                EpisodeTraceRoot = eventData.EpisodeTraceRoot,
                RearmAttempt = eventData.RearmAttempt
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
