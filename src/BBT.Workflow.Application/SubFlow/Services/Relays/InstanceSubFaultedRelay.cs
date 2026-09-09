using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit relay for <see cref="InstanceSubFaultedEvent"/>: faults the parent immediately
/// through the routed gateway. The Inbox handler stays as the durable backup, deduplicated by
/// <c>ISubItemTerminalGuard</c>.
/// </summary>
public sealed class InstanceSubFaultedRelay(IInstanceCommandGateway instanceCommandGateway)
    : SubflowTerminalRelayBase<InstanceSubFaultedEvent>
{
    /// <inheritdoc />
    public override Task<Result> RelayAsync(
        InstanceSubFaultedEvent @event,
        CancellationToken cancellationToken)
        => instanceCommandGateway.FaultAsync(MapToSubFlowFaultedInput(@event), cancellationToken);

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
}
