using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit relay for <see cref="InstanceSubStateChangedEvent"/>: applies a child's state change
/// to its parent's effective state immediately, instead of waiting out outbox → broker → Inbox.
/// The Inbox handler remains the durable backup; both paths land in
/// <c>SubflowStateService.UpdateParentStateAsync</c>, whose per-sub-item lock and monotonic
/// <c>SubFlowStateChangedAt</c> stamp make the duplicate order-safe and idempotent — the equivalent
/// of <c>ISubItemTerminalGuard</c> for this channel.
/// <para>
/// When the parent is itself a subflow, applying the change raises the grandparent's event inside
/// that service's own write, and it hands those events back to the dispatcher after committing —
/// so the fast path continues up the whole ancestor chain rather than stopping at depth 1.
/// </para>
/// </summary>
public sealed class InstanceSubStateChangedRelay(IInstanceCommandGateway instanceCommandGateway)
    : IPostCommitEventRelay<InstanceSubStateChangedEvent>
{
    /// <summary>
    /// Bound for the CROSS-DOMAIN leg only. This channel carries roughly an order of magnitude more
    /// traffic than the three terminal channels combined, and unlike a terminal outcome a single
    /// missed state change is corrected by the next one — so a slow remote must release the child's
    /// hop rather than hold it. Deliberately far below the gateway's own remote default; a constant
    /// rather than configuration, because the opt-in for this whole mode is the DI registration.
    /// </summary>
    private static readonly TimeSpan RemoteRelayTimeout = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    /// <remarks>
    /// The event carries no <c>Sync</c> flag, so that tag stays unwritten for this channel.
    /// </remarks>
    public PostCommitRelayTarget Describe(InstanceSubStateChangedEvent @event)
        => new(@event.Domain, @event.ParentInstanceId, @event.SubInstanceId, Sync: null, RemoteRelayTimeout);

    /// <inheritdoc />
    public Task<Result> RelayAsync(
        InstanceSubStateChangedEvent @event,
        CancellationToken cancellationToken)
        => instanceCommandGateway.UpdateSubFlowStateAsync(MapToInput(@event), cancellationToken);

    /// <summary>
    /// Field-for-field the mapping the Inbox backup builds, so the two delivery paths carry an
    /// identical command — <c>ChangedAt</c> passed through untouched, since it is the ordering
    /// authority the receiver's guard compares against.
    /// </summary>
    private static SubFlowStateChangedInput MapToInput(InstanceSubStateChangedEvent eventData) => new()
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
}
