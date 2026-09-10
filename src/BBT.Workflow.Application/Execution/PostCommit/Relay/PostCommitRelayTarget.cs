namespace BBT.Workflow.Execution.PostCommit.Relay;

/// <summary>
/// Route and tag metadata a <see cref="IPostCommitEventRelay{TEvent}"/> exposes about the event it
/// is about to relay. The dispatcher reads it BEFORE the call so the span carries the same
/// identifiers whether the relay succeeds, fails or times out.
/// </summary>
/// <param name="Domain">Target (parent) domain. The dispatcher tags the route by matching it
/// against the runtime's own domain — the same check <c>RoutedInstanceCommandGateway</c> routes by,
/// so the tag can never disagree with the actual route.</param>
/// <param name="ParentInstanceId">Parent instance the relay addresses.</param>
/// <param name="SubInstanceId">Child instance the event originated from.</param>
/// <param name="Sync">Whether the originating chain executes synchronously end-to-end, when the
/// event carries that fact. <c>null</c> leaves the tag unwritten.</param>
/// <param name="RemoteTimeout">Upper bound for the CROSS-DOMAIN leg only. <c>null</c> — the default
/// for the subflow terminal events — leaves the gateway's own timeout in charge, preserving their
/// historical behaviour. A value bounds the call with a linked CTS and reports
/// <see cref="PostCommitRelayOutcomes.Timeout"/> instead of blocking the child's hop on a slow
/// remote.</param>
public readonly record struct PostCommitRelayTarget(
    string Domain,
    Guid ParentInstanceId,
    Guid SubInstanceId,
    bool? Sync = null,
    TimeSpan? RemoteTimeout = null);

/// <summary>Outcome values written to <c>vnext.relay.outcome</c>.</summary>
public static class PostCommitRelayOutcomes
{
    /// <summary>The command reached the parent and returned success.</summary>
    public const string Relayed = "relayed";

    /// <summary>The relay threw, or the command returned a failed <c>Result</c>.</summary>
    public const string Failed = "failed";

    /// <summary>No relay is registered for the event type — it travels the outbox only.</summary>
    public const string Skipped = "skipped";

    /// <summary>The cross-domain leg exceeded the relay's own bound.</summary>
    public const string Timeout = "timeout";

    /// <summary>The in-process relay chain hit its depth cap; the outbox carries the rest.</summary>
    public const string DepthExceeded = "depth_exceeded";
}
