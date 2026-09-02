using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Workflow.Events;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Default <see cref="ISubflowTerminalRelay"/>. Processes terminal events SEQUENTIALLY: a hop
/// produces at most one terminal event by domain construction (terminal outcomes are exclusive —
/// pinned by SubItemTerminalProbe.Conflict); the loop is defensive, and sequential keeps failure
/// attribution deterministic. Concurrency with the Inbox backup is serialized downstream by the
/// per-subInstance lock + ISubItemTerminalGuard probe in the settlement services.
/// </summary>
public sealed class SubflowTerminalRelay(
    IInstanceCommandGateway instanceCommandGateway,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<SubflowTerminalRelay> logger) : ISubflowTerminalRelay
{
    /// <inheritdoc />
    public async Task RelayAsync(
        IReadOnlyList<DomainEventEnvelope> deferredEvents,
        CancellationToken cancellationToken)
    {
        foreach (var envelope in deferredEvents)
        {
            if (envelope.Event is not ISubflowTerminalEvent terminal)
                continue;

            using var activity = PipelineStepActivityHelper.StartOperationActivity("Subflow.TerminalRelay");
            activity?.SetTag(TelemetryConstants.TagNames.EventName, envelope.Event.GetType().Name);
            activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, terminal.InstanceId);
            activity?.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, terminal.SubInstanceId);
            activity?.SetTag(TelemetryConstants.TagNames.RelaySync, terminal.Sync);
            // Same source the gateway routes by — the tag can never disagree with the actual route.
            activity?.SetTag(TelemetryConstants.TagNames.RelayRoute,
                runtimeInfoProvider.IsDomainMatch(terminal.Domain) ? "local" : "remote");

            try
            {
                var outcome = await DispatchAsync(envelope.Event, cancellationToken);
                activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, outcome);
                if (outcome == "relayed")
                    logger.SubflowTerminalRelayed(envelope.Event.GetType().Name, terminal.SubInstanceId, terminal.InstanceId);
            }
            catch (Exception ex)
            {
                // The child's commit already stands; the outbox row guarantees the Inbox backup
                // settles the parent. Never fail the hop for a relay error.
                activity?.SetTag(TelemetryConstants.TagNames.RelayOutcome, "failed");
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                logger.SubflowTerminalRelayFailed(ex, envelope.Event.GetType().Name, terminal.SubInstanceId, terminal.InstanceId);
            }
        }
    }

    private async Task<string> DispatchAsync(object @event, CancellationToken ct)
    {
        switch (@event)
        {
            case InstanceSubCompletedEvent completed:
            {
                var result = await instanceCommandGateway.CompleteAsync(MapToFlowCompletedInput(completed), ct);
                return HandleResult(result.IsSuccess, @event, result.IsSuccess ? null : result.Error.Message);
            }
            case InstanceSubFaultedEvent faulted:
            {
                var result = await instanceCommandGateway.FaultAsync(MapToSubFlowFaultedInput(faulted), ct);
                return HandleResult(result.IsSuccess, @event, result.IsSuccess ? null : result.Error.Message);
            }
            case InstanceSubCanceledEvent canceled:
            {
                var result = await instanceCommandGateway.CancelAsync(MapToSubItemCanceledInput(canceled), ct);
                return HandleResult(result.IsSuccess, @event, result.IsSuccess ? null : result.Error.Message);
            }
            default:
                return "skipped";
        }
    }

    private string HandleResult(bool success, object @event, string? error)
    {
        if (success)
            return "relayed";
        logger.SubflowTerminalRelayRejected(@event.GetType().Name, error ?? "unknown");
        return "failed";
    }

    /// <summary>
    /// Maps the event data to FlowCompletedInput DTO.
    /// Moved verbatim from <c>InstanceSubCompletedEventHook.MapToFlowCompletedInput</c>.
    /// </summary>
    private static FlowCompletedInput MapToFlowCompletedInput(InstanceSubCompletedEvent eventData)
    {
        return new FlowCompletedInput
        {
            InstanceId = eventData.InstanceId,
            RootInstanceId = eventData.RootInstanceId,
            Domain = eventData.Domain,
            Flow = eventData.Flow,
            CompletedAt = eventData.CompletedAt,
            CompletedState = eventData.CompletedState,
            Duration = eventData.Duration,
            SubInstanceId = eventData.SubInstanceId,
            InstanceData = eventData.InstanceData,
            Version = eventData.Version,
            Sync = eventData.Sync,
            // Carry the subflow's lane so the parent resume on the other side lands in the parent's
            // lane (ParentTraceRoot) instead of nesting under the completion relay endpoint.
            TraceRoot = eventData.TraceRoot,
            ParentTraceRoot = eventData.ParentTraceRoot,
            EpisodeStartedAt = eventData.EpisodeStartedAt,
            EpisodeTrigger = eventData.EpisodeTrigger,
            EpisodeTransitionKey = eventData.EpisodeTransitionKey,
            EpisodeTraceRoot = eventData.EpisodeTraceRoot,
            RearmAttempt = eventData.RearmAttempt
        };
    }

    /// <summary>
    /// Moved verbatim from <c>InstanceSubFaultedEventHook.MapToSubFlowFaultedInput</c>.
    /// </summary>
    private static SubFlowFaultedInput MapToSubFlowFaultedInput(InstanceSubFaultedEvent eventData)
    {
        return new SubFlowFaultedInput
        {
            InstanceId = eventData.InstanceId,
            Domain = eventData.Domain,
            Flow = eventData.Flow,
            Version = eventData.Version,
            SubInstanceId = eventData.SubInstanceId,
            FaultedState = eventData.FaultedState,
            FaultedStateType = eventData.FaultedStateType,
            FaultedStateSubType = eventData.FaultedStateSubType,
            InstanceData = eventData.InstanceData,
            FaultedAt = eventData.FaultedAt,
            SubFlowName = eventData.SubFlowName,
            IncidentMessage = eventData.IncidentMessage,
            IncidentErrorCode = eventData.IncidentErrorCode,
            IncidentErrorLayer = eventData.IncidentErrorLayer,
            IncidentStackTrace = eventData.IncidentStackTrace,
            IncidentStatusCode = eventData.IncidentStatusCode,
            IncidentTraceId = eventData.IncidentTraceId,
            IncidentTaskKey = eventData.IncidentTaskKey,
            IncidentTransition = eventData.IncidentTransition,
            IncidentState = eventData.IncidentState,
            IncidentBoundaryAction = eventData.IncidentBoundaryAction,
            IncidentBoundaryLevel = eventData.IncidentBoundaryLevel,
            RootInstanceId = eventData.RootInstanceId,
            SubItemType = eventData.SubItemType ?? SubItemType.SubFlow,
            Termination = eventData.CascadeId.HasValue && eventData.InitiatorInstanceId.HasValue
                ? new TerminationContext(
                    eventData.TerminationOrigin ?? TerminationOrigin.Direct,
                    eventData.InitiatorInstanceId.Value,
                    eventData.CascadeId.Value)
                : null,
            Sync = eventData.Sync,
            TraceRoot = eventData.TraceRoot,
            ParentTraceRoot = eventData.ParentTraceRoot,
            EpisodeStartedAt = eventData.EpisodeStartedAt,
            EpisodeTrigger = eventData.EpisodeTrigger,
            EpisodeTransitionKey = eventData.EpisodeTransitionKey,
            EpisodeTraceRoot = eventData.EpisodeTraceRoot,
            RearmAttempt = eventData.RearmAttempt
        };
    }

    /// <summary>
    /// Moved verbatim from <c>InstanceSubCanceledEventHook.Map</c>.
    /// </summary>
    private static SubItemCanceledInput MapToSubItemCanceledInput(InstanceSubCanceledEvent eventData) => new()
    {
        InstanceId = eventData.InstanceId,
        SubInstanceId = eventData.SubInstanceId,
        Domain = eventData.Domain,
        Flow = eventData.Flow,
        Version = eventData.Version,
        CanceledState = eventData.CanceledState,
        CanceledAt = eventData.CanceledAt,
        RootInstanceId = eventData.RootInstanceId,
        Sync = eventData.Sync,
        Termination = new TerminationContext(
            eventData.TerminationOrigin,
            eventData.InitiatorInstanceId,
            eventData.CascadeId),
        // Carry the subflow's lane so the parent resume on the other side lands in the parent's
        // lane (ParentTraceRoot) instead of nesting under the cancellation relay endpoint — same
        // as MapToFlowCompletedInput/MapToSubFlowFaultedInput above.
        TraceRoot = eventData.TraceRoot,
        ParentTraceRoot = eventData.ParentTraceRoot,
        EpisodeStartedAt = eventData.EpisodeStartedAt,
        EpisodeTrigger = eventData.EpisodeTrigger,
        EpisodeTransitionKey = eventData.EpisodeTransitionKey,
        EpisodeTraceRoot = eventData.EpisodeTraceRoot,
        RearmAttempt = eventData.RearmAttempt
    };
}
