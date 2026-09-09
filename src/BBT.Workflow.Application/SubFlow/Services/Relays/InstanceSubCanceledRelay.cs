using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit relay for <see cref="InstanceSubCanceledEvent"/>: cancels the parent's sub-item
/// immediately through the routed gateway. The Inbox handler stays as the durable backup,
/// deduplicated by <c>ISubItemTerminalGuard</c>.
/// </summary>
public sealed class InstanceSubCanceledRelay(IInstanceCommandGateway instanceCommandGateway)
    : SubflowTerminalRelayBase<InstanceSubCanceledEvent>
{
    /// <inheritdoc />
    public override Task<Result> RelayAsync(
        InstanceSubCanceledEvent @event,
        CancellationToken cancellationToken)
        => instanceCommandGateway.CancelAsync(MapToSubItemCanceledInput(@event), cancellationToken);

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
        // as the completed/faulted mappers.
        TraceRoot = eventData.TraceRoot,
        ParentTraceRoot = eventData.ParentTraceRoot,
        EpisodeStartedAt = eventData.EpisodeStartedAt,
        EpisodeTrigger = eventData.EpisodeTrigger,
        EpisodeTransitionKey = eventData.EpisodeTransitionKey,
        EpisodeTraceRoot = eventData.EpisodeTraceRoot,
        RearmAttempt = eventData.RearmAttempt
    };
}
