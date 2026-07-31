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
/// Controls how long <see cref="ITransitionLockScopeFactory.AcquireAsync(string, LockAcquireWait, CancellationToken)"/>
/// keeps retrying before reporting the lock as not acquired.
/// <para>
/// The default (<see cref="None"/>) is a single attempt — the historical fail-fast behaviour the
/// transition pipeline relies on to surface a busy instance immediately.
/// </para>
/// </summary>
/// <param name="MaxAttempts">Total acquisition attempts, including the first. Must be at least 1.</param>
/// <param name="Delay">Base delay between attempts. Jitter is applied by the implementation.</param>
public readonly record struct LockAcquireWait(int MaxAttempts, TimeSpan Delay)
{
    /// <summary>
    /// Single attempt, no waiting. Preserves the original fail-fast semantics.
    /// </summary>
    public static LockAcquireWait None => new(1, TimeSpan.Zero);
}

/// <summary>
/// Factory for acquiring request-scoped transition lock scopes.
/// </summary>
public interface ITransitionLockScopeFactory
{
    /// <summary>
    /// Acquires a lock scope for the given lock key using a single attempt (fail-fast).
    /// The returned scope should be disposed when the entire transition chain completes.
    /// </summary>
    /// <param name="lockKey">Instance-level lock key (e.g. <c>vnext:{domain}:{flow}:{instanceId}</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lock scope. Check <see cref="ITransitionLockScope.IsAcquired"/> before proceeding.</returns>
    Task<ITransitionLockScope> AcquireAsync(string lockKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acquires a lock scope for the given lock key, retrying according to <paramref name="wait"/>.
    /// <para>
    /// The default implementation ignores <paramref name="wait"/> and delegates to the fail-fast
    /// overload, so implementations written before this member existed keep compiling and keep
    /// their original behaviour. Implementations that can honour a retry budget should override it.
    /// </para>
    /// </summary>
    /// <param name="lockKey">Instance-level lock key (e.g. <c>vnext:{domain}:{flow}:{instanceId}</c>).</param>
    /// <param name="wait">Retry policy. Use <see cref="LockAcquireWait.None"/> for fail-fast.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lock scope. Check <see cref="ITransitionLockScope.IsAcquired"/> before proceeding.</returns>
    Task<ITransitionLockScope> AcquireAsync(
        string lockKey,
        LockAcquireWait wait,
        CancellationToken cancellationToken = default)
        => AcquireAsync(lockKey, cancellationToken);
}
