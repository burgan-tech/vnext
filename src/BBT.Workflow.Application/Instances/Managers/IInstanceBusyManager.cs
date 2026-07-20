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
}
