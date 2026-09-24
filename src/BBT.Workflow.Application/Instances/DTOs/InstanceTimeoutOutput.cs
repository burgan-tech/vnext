namespace BBT.Workflow.Instances;

/// <summary>
/// The workflow-level deadline currently armed for the polled instance, surfaced on the State
/// (long-poll) function so a client can render a countdown — "auto-cancels at HH:MM" — without
/// polling anything else. Present only while a deadline is genuinely pending; omitted entirely
/// otherwise, so a client branches on the property's presence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a transition, and deliberately not in <c>transitions</c>.</b> A workflow timeout is
/// instance-scoped rather than state-scoped, armed exactly once at start and never re-armed
/// (unlike a <c>kind: "scheduled"</c> entry, which a state cancels on exit and re-arms on entry),
/// and it is keyed by the virtual <c>$timeout</c> — there is no callable transition behind it. It
/// therefore cannot honour the uniform <c>href</c>/<c>view</c>/<c>schema</c> shape every
/// <c>transitions[]</c> item carries, and <c>TransitionItem</c> has nowhere to put
/// <see cref="Target"/>. It rides as its own block, like <c>incident</c> and <c>interaction</c>.
/// </para>
/// <para>
/// <b>Always the polled instance.</b> The block is never merged from, nor descended into, an
/// active subflow — the same rule the scheduled entries follow. Poll the subflow instance for its
/// own deadline.
/// </para>
/// <para>
/// <b>Emitted only while the deadline can still fire.</b> Suppressed as soon as the polled
/// instance's own status is terminal, which matters because the job row is closed asynchronously
/// (an <c>InstanceCompletedCleanupEvent</c> travelling outbox → Dapr → Inbox → <c>cancel-cleanup</c>),
/// so a finished instance can still carry an active timeout row for a while — or indefinitely, if
/// that chain is degraded. The guard makes the block independent of it.
/// </para>
/// <para>
/// <b>Adds no staleness behind a 304.</b> <see cref="ExecuteAtUtc"/> is resolved once when the
/// scheduler is armed and never moves, and the block's presence is governed by the instance status,
/// which is already fingerprint material — unlike the scheduled entries beside it, whose job-set
/// changes are deliberately outside the ETag (issue #864).
/// </para>
/// </remarks>
public sealed class InstanceTimeoutOutput
{
    /// <summary>
    /// The timeout's declared key (<c>timeout.key</c> in the workflow definition, or the
    /// parent-supplied SubFlow override's key when the instance was started with one). A label for
    /// the deadline, not a transition key — the runtime records the move under the virtual
    /// <c>$timeout</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The state the instance will be pulled to when the deadline fires (<c>timeout.target</c>),
    /// so a client can say what is about to happen and not merely when. Resolved from the same
    /// effective timeout the runtime will act on.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// The UTC instant the scheduler was armed to fire the timeout at, read from the persisted job
    /// row rather than recomputed — the exact instant the arm used, mapping script included. Always
    /// <see cref="DateTimeKind.Utc"/>, so it serializes with the <c>Z</c> designator. May be in the
    /// past for a moment while a fired timeout's pipeline is still settling.
    /// </summary>
    public DateTime ExecuteAtUtc { get; set; }

    /// <summary>
    /// The effective timeout's <c>annotations</c> (<c>timeout.annotations</c>, or the parent-supplied
    /// SubFlow override's when the instance was started with one — the override replaces, it does not
    /// merge). Pure passthrough for client UI context; null — and omitted — when none are declared.
    /// </summary>
    public Dictionary<string, string>? Annotations { get; set; }
}
