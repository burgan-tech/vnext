namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Represents a request-scoped lock that lives for the entire transition chain.
/// Auto-chained transitions share the same lock scope — no gap between iterations.
/// Automatically releases the lock on dispose.
/// </summary>
public interface ITransitionLockScope : IAsyncDisposable
{
    /// <summary>
    /// Gets a value indicating whether the lock was successfully acquired.
    /// </summary>
    bool IsAcquired { get; }

    /// <summary>
    /// Gets the lock key that this scope protects.
    /// </summary>
    string LockKey { get; }

    /// <summary>
    /// Extends the lock TTL. Call between auto-chained transitions
    /// to prevent expiry during long-running chains.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the TTL was extended; <c>false</c> if the lock was lost.</returns>
    Task<bool> ExtendAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Factory for acquiring request-scoped transition lock scopes.
/// </summary>
public interface ITransitionLockScopeFactory
{
    /// <summary>
    /// Acquires a lock scope for the given lock key.
    /// The returned scope should be disposed when the entire transition chain completes.
    /// </summary>
    /// <param name="lockKey">Instance-level lock key (e.g. <c>vnext:{domain}:{flow}:{instanceId}</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lock scope. Check <see cref="ITransitionLockScope.IsAcquired"/> before proceeding.</returns>
    Task<ITransitionLockScope> AcquireAsync(string lockKey, CancellationToken cancellationToken = default);
}
