using BBT.Aether.Events;

namespace BBT.Workflow.Execution.PostCommit.Relay;

/// <summary>
/// Relays post-commit events as immediate commands. Selects, per envelope, the
/// <see cref="IPostCommitEventRelay{TEvent}"/> registered for that event's runtime type; an event
/// with no registration is skipped and travels the outbox alone.
/// <para>
/// Called AFTER the originating unit of work has committed, and awaited: a sync chain's response
/// then follows the settled chain, and an async job relays with gap ≈ 0. Failures are logged and
/// swallowed — the commit stands regardless and the durable backup converges.
/// </para>
/// </summary>
public interface IPostCommitRelayDispatcher
{
    /// <summary>
    /// Relays every envelope that has a registered relay, sequentially. Never throws.
    /// </summary>
    Task RelayAsync(IReadOnlyList<DomainEventEnvelope> deferredEvents, CancellationToken cancellationToken);
}
