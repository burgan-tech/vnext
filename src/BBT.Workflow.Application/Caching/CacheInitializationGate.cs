namespace BBT.Workflow.Caching;

/// <summary>
/// Singleton per-pod gate that prevents concurrent cache initializations and tracks
/// the last successful initialization time for incremental (delta) loads.
/// </summary>
public sealed class CacheInitializationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private DateTime? _lastInitializedAt;

    /// <summary>
    /// Attempts to acquire the initialization lock without blocking.
    /// Returns <c>true</c> if the lock was acquired; <c>false</c> if initialization is already in progress.
    /// </summary>
    public bool TryAcquire() => _semaphore.Wait(0);

    /// <summary>Releases the initialization lock.</summary>
    public void Release() => _semaphore.Release();

    /// <summary>
    /// UTC timestamp of the last successful cache initialization, or <c>null</c> if
    /// the cache has never been initialized on this pod instance.
    /// </summary>
    public DateTime? LastInitializedAt => _lastInitializedAt;

    /// <summary>Records a successful initialization at the given UTC time.</summary>
    public void SetLastInitializedAt(DateTime utcNow) => _lastInitializedAt = utcNow;
}
