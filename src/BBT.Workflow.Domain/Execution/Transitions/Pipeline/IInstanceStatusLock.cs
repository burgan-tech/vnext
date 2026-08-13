namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Short-lease distributed lock that protects instance status transitions only
/// (Active→Busy reserve, Busy→Active/Completed/Faulted settlement). Unlike
/// <see cref="ITransitionLockScopeFactory"/>, whose lease is sized to cover a whole
/// auto-chain, this lock is held for the milliseconds it takes to check-and-set the
/// status column — the Busy flag itself is the logical mutex for pipeline execution.
/// <para>
/// The scope is chain-reentrant: when the legacy whole-chain lock already holds the
/// same key higher up the async call chain, acquisition returns a reentrant scope so
/// settlement code works identically in both locking modes.
/// </para>
/// </summary>
public interface IInstanceStatusLock
{
    /// <summary>
    /// Acquires the status lock for the given instance lock key using the configured
    /// bounded retry (status flips are short; contention resolves in milliseconds).
    /// </summary>
    /// <param name="lockKey">Instance-level lock key (<c>vnext:{domain}:{flow}:{instanceId}</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lock scope. Check <see cref="ITransitionLockScope.IsAcquired"/> before proceeding.</returns>
    Task<ITransitionLockScope> AcquireAsync(string lockKey, CancellationToken cancellationToken = default);
}
