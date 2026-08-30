namespace BBT.Workflow.Events;

/// <summary>
/// Declares the "Outbox + TerminalRelay" publish mode: the event still rides the transactional
/// outbox as a durable fact (Inbox handler = backup, deduplicated by ISubItemTerminalGuard), and
/// the transition runner ADDITIONALLY relays it as an immediate post-commit command so the parent
/// settles with gap ≈ 0 — inline for the same domain, one Dapr invocation across domains. The
/// marker interface IS the mode declaration: the terminal set is closed by the subflow protocol,
/// so no attribute/enum registry is warranted.
/// </summary>
public interface ISubflowTerminalEvent
{
    /// <summary>Target (parent) domain the terminal processing routes to.</summary>
    string Domain { get; }

    /// <summary>True when the originating chain executes synchronously end-to-end.</summary>
    bool Sync { get; }

    /// <summary>Parent instance the relay settles.</summary>
    Guid InstanceId { get; }

    /// <summary>Terminal child instance.</summary>
    Guid SubInstanceId { get; }
}
