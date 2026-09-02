using BBT.Aether.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Post-commit command relay for subflow terminal events (Outbox + TerminalRelay mode).
/// Selects <c>ISubflowTerminalEvent</c> payloads from the hop's deferred events and settles the parent
/// immediately through the routed gateway — inline for the same domain, one Dapr service
/// invocation across domains. CallerMode follows the event's Sync flag, so a sync chain stays
/// sync end-to-end. Failures are logged and swallowed: the child's commit already stands, and the
/// event's outbox row guarantees the Inbox backup settles the parent shortly after.
/// </summary>
public interface ISubflowTerminalRelay
{
    /// <summary>
    /// Relays every subflow terminal event found in <paramref name="deferredEvents"/> to the parent
    /// instance through <c>IInstanceCommandGateway</c>. Non-terminal events are ignored. Relay
    /// failures (exception or a failed <c>Result</c>) are logged and swallowed — never rethrown —
    /// because the durable Inbox backup will settle the parent regardless.
    /// </summary>
    /// <param name="deferredEvents">The hop's deferred domain event envelopes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RelayAsync(IReadOnlyList<DomainEventEnvelope> deferredEvents, CancellationToken cancellationToken);
}
