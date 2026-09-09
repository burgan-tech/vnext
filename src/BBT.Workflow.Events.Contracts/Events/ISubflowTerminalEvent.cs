namespace BBT.Workflow.Events;

/// <summary>
/// The shape the three subflow terminal events share: which parent a terminal outcome settles, and
/// which child produced it. The post-commit relays for those events read their route and span tags
/// from here, so the description is written once rather than three times.
/// <para>
/// This is NOT the publish-mode declaration. Dual delivery (outbox + post-commit relay) is granted
/// per event type by REGISTRATION — an <c>IPostCommitEventRelay&lt;TEvent&gt;</c> in DI — so an
/// event opts in without implementing any marker, and implementing this one does not opt anything
/// in. See <c>docs/runtime/event-publish-modes.md</c>.
/// </para>
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
