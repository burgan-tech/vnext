namespace BBT.Workflow.Execution;

/// <summary>
/// Provides distributed resource locking with explicit Acquire / Release / Extend semantics.
/// Used by the transition pipeline to protect shared resources (e.g., time-slots, seats)
/// across workflow instances.
/// </summary>
public interface IResourceLockService
{
    /// <summary>
    /// Attempts to acquire a lock on the specified resource.
    /// </summary>
    /// <param name="resourceKey">Unique key identifying the resource (e.g., "seat:concert1:A1").</param>
    /// <param name="owner">Lock owner identifier, typically the workflow instance ID.</param>
    /// <param name="ttlSeconds">Lock time-to-live in seconds. The lock expires automatically after this duration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the lock was acquired; <c>false</c> if the resource is already locked by another owner.</returns>
    Task<bool> AcquireAsync(string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken);

    /// <summary>
    /// Releases a previously acquired lock on the specified resource.
    /// </summary>
    /// <param name="resourceKey">Unique key identifying the resource.</param>
    /// <param name="owner">Lock owner identifier that originally acquired the lock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the lock was released; <c>false</c> if the lock was not held by this owner.</returns>
    Task<bool> ReleaseAsync(string resourceKey, string owner, CancellationToken cancellationToken);

    /// <summary>
    /// Extends the TTL of an existing lock held by the same owner.
    /// Implemented as a re-acquire with the new TTL.
    /// </summary>
    /// <param name="resourceKey">Unique key identifying the resource.</param>
    /// <param name="owner">Lock owner identifier that currently holds the lock.</param>
    /// <param name="ttlSeconds">New TTL in seconds from now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the lock was extended; <c>false</c> if the lock is not held by this owner.</returns>
    Task<bool> ExtendAsync(string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken);
}
