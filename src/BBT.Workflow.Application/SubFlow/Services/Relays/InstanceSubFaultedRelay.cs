using BBT.Aether.Results;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit relay for <see cref="InstanceSubFaultedEvent"/>: faults a parent hosted in ANOTHER
/// domain immediately through the routed gateway (vnext#1256). That parent has no other delivery:
/// the child domain's Inbox drops a fault whose parent it does not host.
/// <para>
/// A parent hosted here is skipped on purpose and keeps the Inbox delivery it always had. Relaying
/// it in-process would run the parent's whole error-boundary chain inside the child's request, on
/// whatever is left of the child's run budget (a 15 s compensation budget on the fault paths) and
/// partly on the child's still-open unit of work — the council session
/// <c>2026-11-23-sub-fault-relay-1256</c> rejected that for this fix. The Inbox path settles the
/// parent on a fresh run budget, deduplicated by <c>ISubItemTerminalGuard</c>. This makes fault
/// delivery depend on topology when domains are co-hosted (<c>APP_DOMAINS</c>): a co-hosted parent
/// is "hosted here" and goes through the Inbox.
/// </para>
/// </summary>
public sealed class InstanceSubFaultedRelay(
    IInstanceCommandGateway instanceCommandGateway,
    IRuntimeInfoProvider runtimeInfoProvider)
    : SubflowTerminalRelayBase<InstanceSubFaultedEvent>
{
    /// <summary>Skip reason for a parent hosted by this runtime; its Inbox delivers the fault.</summary>
    public const string ParentHostedHereReason = "parent_hosted_here";

    /// <inheritdoc />
    public override PostCommitRelayTarget Describe(InstanceSubFaultedEvent @event)
    {
        var target = base.Describe(@event);
        return runtimeInfoProvider.IsDomainMatch(@event.Domain)
            ? target with { SkipReason = ParentHostedHereReason }
            : target;
    }
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
            CarriedHeaders = eventData.CarriedHeaders,
            RearmAttempt = eventData.RearmAttempt
        };
    }
}
