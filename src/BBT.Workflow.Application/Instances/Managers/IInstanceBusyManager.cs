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
    /// <returns>
    /// True when this call actually changed the status. Callers that may have to compensate the
    /// flip need it: releasing a Busy that was already held by someone else would free another
    /// owner's instance.
    /// </returns>
    Task<bool> MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);

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
    /// Releases a reservation whose follow-up work never ran (compensation): settles Busy back
    /// to Active. No-ops when the instance is not Busy or is Completed. Must be called under
    /// the short status lock and only by the accept path that performed the reserve.
    /// </summary>
    Task<bool> TryReleaseAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compensating mirror of <see cref="MarkBusyWithPropagationAsync"/>: walks the active SubFlow
    /// chain and settles back to Active only the instances the chain reserve actually flipped.
    /// <para>
    /// An instance holding an open SubFlow correlation is Busy for that subflow's whole lifetime by
    /// design (<c>Instance.AddCorrelation</c>), so the chain reserve never flipped it and this
    /// release must NOT clear it — it only recurses past it. Effectively only the leaf is released.
    /// Same rule the settlement path re-checks before resolving a deferred Active status.
    /// </para>
    /// </summary>
    Task ReleaseWithPropagationAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
