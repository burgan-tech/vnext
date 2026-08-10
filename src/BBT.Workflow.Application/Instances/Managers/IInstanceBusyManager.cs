namespace BBT.Workflow.Instances;

/// <summary>
/// Outcome of an attempt to mark an instance as Busy.
/// </summary>
public enum BusyMarkOutcome
{
    /// <summary>The instance was transitioned to Busy.</summary>
    Marked = 0,

    /// <summary>The instance was already Busy — a transition is queued or executing.</summary>
    AlreadyBusy = 1,

    /// <summary>No mark applied: the instance was not found or is Completed.</summary>
    Skipped = 2
}

/// <summary>
/// Manages the Busy status of workflow instances with isolated transactions.
/// Consolidates pre-pipeline busy marking, async pre-enqueue marking, and SubFlow chain propagation.
/// </summary>
public interface IInstanceBusyManager
{
    /// <summary>
    /// Marks a single instance as Busy in an isolated RequiresNew transaction.
    /// Idempotent: silently no-ops when the instance is already Busy, Completed, or not found.
    /// </summary>
    Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an instance as Busy and propagates down the active SubFlow chain via the
    /// instance command gateway (cross-domain capable).
    /// Idempotent: silently no-ops when the instance is already Busy or Completed.
    /// </summary>
    Task MarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to mark an instance as Busy with SubFlow propagation, reporting the prior state.
    /// Unlike <see cref="MarkBusyWithPropagationAsync"/>, an already-Busy instance short-circuits
    /// with <see cref="BusyMarkOutcome.AlreadyBusy"/> (no propagation) so callers can reject the
    /// request instead of silently proceeding.
    /// </summary>
    Task<BusyMarkOutcome> TryMarkBusyWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to reserve an instance for the Busy-as-mutex model: like
    /// <see cref="TryMarkBusyWithPropagationAsync"/>, but stamps the supplied chain ownership
    /// token (<see cref="Instance.BeginChain"/>) instead of a bare Busy flip, so the reserving
    /// request owns the instance. Must be called under the short status lock — this method
    /// performs the check, not the mutual exclusion.
    /// </summary>
    Task<BusyMarkOutcome> TryReserveWithPropagationAsync(
        Guid instanceId, Guid chainToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes over an instance regardless of its Busy state (cancel/exit/timeout): rotates the
    /// chain ownership token and marks Busy in an isolated RequiresNew transaction. Rotating
    /// the token invalidates any in-flight chain — it detects the mismatch at its next
    /// ownership check or status write. Returns <see cref="BusyMarkOutcome.Skipped"/> when the
    /// instance is Completed or not found. Must be called under the short status lock.
    /// </summary>
    Task<BusyMarkOutcome> TakeOverAsync(
        Guid instanceId, Guid chainToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a reservation whose follow-up work never ran (compensation): settles Busy back
    /// to Active only when the instance still carries the supplied chain token. No-ops on a
    /// token mismatch — a takeover or reaper already owns the instance. Must be called under
    /// the short status lock.
    /// </summary>
    Task<bool> TryReleaseAsync(
        Guid instanceId, Guid chainToken, CancellationToken cancellationToken = default);
}
