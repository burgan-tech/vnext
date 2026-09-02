using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Outcome of the pre-lock terminal settlement probe.
/// </summary>
public enum SubItemTerminalProbe
{
    /// <summary>
    /// Settlement could not be proven from the read-only snapshot — the terminal outcome is not
    /// persisted yet, the correlation is not visible, or the sub-item type does not allow a
    /// lock-free decision. The caller must acquire the lock and re-evaluate authoritatively.
    /// </summary>
    Proceed = 0,

    /// <summary>
    /// The same terminal outcome is already persisted <em>and</em> fully settled — this delivery is
    /// a duplicate and the caller can return successfully without touching the distributed lock.
    /// </summary>
    AlreadySettled = 1,

    /// <summary>
    /// A <em>different</em> terminal outcome already settled the correlation. It is closed, so the
    /// caller must not reopen it; the incoming outcome is dropped.
    /// </summary>
    Conflict = 2
}

/// <summary>
/// Lock-free pre-check for sub-item terminal deliveries (Completed / Faulted / Canceled).
/// <para>
/// Terminal sub-item events are delivered twice by design: once by the post-commit terminal relay
/// and once through the Inbox backup. On top of that the broker guarantees only at-least-once
/// delivery. Without this
/// probe, each duplicate has to win the per-subInstance distributed lock purely to discover that
/// the work is already done — and a duplicate that loses the race is reported as a transient
/// failure, forcing a full broker re-delivery cycle.
/// </para>
/// <para>
/// <b>Why this only answers for <see cref="SubFlowType.SubProcess"/>:</b> a persisted terminal
/// outcome is not by itself proof that the delivery has been fully discharged. A blocking
/// <see cref="SubFlowType.SubFlow"/> completes its correlation, <em>releases the lock</em>, and
/// only then resumes the parent in a second phase; if that resume fails the correlation is
/// reverted so the delivery can be retried. Acknowledging such a delivery from the persisted flag
/// alone would consume a durable message whose work is about to be rolled back. A SubProcess has
/// no second phase — it commits the correlation and returns — so there the flag really is final.
/// Widening this fast path to SubFlow requires a durable settlement marker first.
/// </para>
/// <para>
/// This guard is an optimisation only: whenever the answer is not conclusive it returns
/// <see cref="SubItemTerminalProbe.Proceed"/> and the authoritative locked path decides.
/// </para>
/// </summary>
public interface ISubItemTerminalGuard
{
    /// <summary>
    /// Probes whether the given terminal outcome has already been applied <em>and settled</em>.
    /// </summary>
    /// <param name="parentInstanceId">Parent instance owning the correlation.</param>
    /// <param name="subInstanceId">Sub-flow / sub-process instance that reached a terminal state.</param>
    /// <param name="incomingOutcome">The terminal outcome carried by this delivery.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <see cref="SubItemTerminalProbe.AlreadySettled"/> when this is a duplicate of an identical
    /// outcome that is provably settled, <see cref="SubItemTerminalProbe.Conflict"/> when a
    /// different settled outcome already closed the correlation, otherwise
    /// <see cref="SubItemTerminalProbe.Proceed"/>.
    /// </returns>
    Task<SubItemTerminalProbe> ProbeAsync(
        Guid parentInstanceId,
        Guid subInstanceId,
        SubItemTerminalOutcome incomingOutcome,
        CancellationToken cancellationToken = default);
}
