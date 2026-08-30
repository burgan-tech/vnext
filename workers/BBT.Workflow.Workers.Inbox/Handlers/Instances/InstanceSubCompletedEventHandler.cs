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
/// Forwards <see cref="InstanceSubCompletedEvent"/> to the Orchestration <c>complete</c> internal
/// endpoint via Dapr service invocation. Thin relay: no domain processing here — Orchestration's
/// <c>ISubflowCompletionService</c> runs the completion. Domain guard stays local to avoid
/// forwarding cross-domain events.
/// </summary>
internal sealed class InstanceSubCompletedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IOrchestrationForwarder forwarder,
    ICorrelationIdProvider correlationIdProvider,
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

        using var traceScope = EventTraceScope.Start(
            "InstanceSubCompleted.Handle", eventData, correlationIdProvider,
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
            [TelemetryConstants.TagNames.SubItemType] = "N/A",
            [TelemetryConstants.TagNames.SubItemOutcome] = "Completed",
            [TelemetryConstants.TagNames.TerminationOrigin] = "legacy",
            [TelemetryConstants.TagNames.TerminationInitiator] = "N/A",
            [TelemetryConstants.TagNames.TerminationCascadeId] = "N/A"
        };
        if (eventData.RootInstanceId.HasValue)
        {
            Activity.Current?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId,
                eventData.RootInstanceId.Value.ToString());
        }
        using (logger.BeginScope(scopeProps))
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
                RootInstanceId = eventData.RootInstanceId,
                Domain = eventData.Domain,
                Flow = eventData.Flow,
                Version = eventData.Version,
                CompletedState = eventData.CompletedState,
                InstanceData = eventData.InstanceData,
                CompletedAt = eventData.CompletedAt,
                Duration = eventData.Duration,
                // At-least-once async retry path: the sync caller (if any) was already answered
                // by the synchronous hook. Force async here so a retried resume never blocks the
                // worker with an inline sync chain; idempotent guards make duplicates no-ops.
                Sync = false,
                // Relay the lane so the parent resume on the receiving side lands at the parent
                // instance's level rather than nesting under the relay endpoint.
                TraceRoot = eventData.TraceRoot,
                ParentTraceRoot = eventData.ParentTraceRoot,
                RearmAttempt = eventData.RearmAttempt
            };

            var route = $"api/v1/{eventData.Domain}/workflows/{eventData.Flow}/instances/{eventData.InstanceId}/complete";
            await forwarder.ForwardAsync(HttpMethod.Post, route, body,
                eventData.Domain, eventData.Flow, eventData.Version, eventData.InstanceId, cancellationToken);
        }
    }
}
