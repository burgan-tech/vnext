using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit relay for <see cref="InstanceSubCompletedEvent"/>: settles the parent immediately
/// through the routed gateway. The Inbox handler stays as the durable backup, deduplicated by
/// <c>ISubItemTerminalGuard</c>.
/// </summary>
public sealed class InstanceSubCompletedRelay(IInstanceCommandGateway instanceCommandGateway)
    : SubflowTerminalRelayBase<InstanceSubCompletedEvent>
{
    /// <inheritdoc />
    public override Task<Result> RelayAsync(
        InstanceSubCompletedEvent @event,
        CancellationToken cancellationToken)
        => instanceCommandGateway.CompleteAsync(MapToFlowCompletedInput(@event), cancellationToken);

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
}
