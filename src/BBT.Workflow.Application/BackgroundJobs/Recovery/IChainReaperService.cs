namespace BBT.Workflow.BackgroundJobs.Recovery;

/// <summary>
/// Detects instances stuck in Busy status with an active auto-chain token whose heartbeat is
/// stale and that have no live/pending job, then resolves them (fault). Backstop for the
/// transition-per-job model where chain ownership is durable state rather than a held lock (S7).
/// </summary>
public interface IChainReaperService
{
    /// <summary>
    /// Sweeps the current schema for stuck chains and resolves them.
    /// </summary>
    /// <returns>The number of instances faulted.</returns>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}
