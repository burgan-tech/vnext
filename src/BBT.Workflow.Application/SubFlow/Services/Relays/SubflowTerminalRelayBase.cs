using BBT.Aether.Results;
using BBT.Workflow.Events;
using BBT.Workflow.Execution.PostCommit.Relay;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Shared shape for the three subflow terminal relays. <see cref="ISubflowTerminalEvent"/> already
/// carries every field the dispatcher tags by, so the route description is written once here and
/// each relay only supplies its own command mapping.
/// <para>
/// <see cref="PostCommitRelayTarget.RemoteTimeout"/> is deliberately left null: the terminal relays
/// have always run under the gateway's own timeout, and a settled parent is worth waiting for. The
/// higher-volume sub-state relay is the one that bounds its remote leg.
/// </para>
/// </summary>
public abstract class SubflowTerminalRelayBase<TEvent> : IPostCommitEventRelay<TEvent>
    where TEvent : class, ISubflowTerminalEvent
{
    /// <inheritdoc />
    public PostCommitRelayTarget Describe(TEvent @event)
        => new(@event.Domain, @event.InstanceId, @event.SubInstanceId, @event.Sync);

    /// <inheritdoc />
    public abstract Task<Result> RelayAsync(TEvent @event, CancellationToken cancellationToken);
}
