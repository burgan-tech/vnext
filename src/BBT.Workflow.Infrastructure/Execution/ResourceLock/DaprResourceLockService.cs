#pragma warning disable DAPR_DISTRIBUTEDLOCK

using Dapr.Client;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.Execution.ResourceLock;

/// <summary>
/// Dapr-backed implementation of <see cref="IResourceLockService"/>.
/// Uses the Dapr distributed lock building block (lock.redis) for
/// explicit Acquire / Release / Extend semantics.
/// </summary>
public sealed class DaprResourceLockService(
    DaprClient daprClient,
    string lockStoreName,
    ILogger<DaprResourceLockService> logger) : IResourceLockService
{
    /// <inheritdoc />
    public async Task<bool> AcquireAsync(
        string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken)
    {
        var response = await daprClient.Lock(
            lockStoreName, resourceKey, owner, ttlSeconds, cancellationToken);

        if (response.Success)
        {
            logger.ResourceLockAcquired(resourceKey, owner, ttlSeconds);
            return true;
        }

        logger.ResourceLockAcquireConflict(resourceKey, owner);
        return false;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Release is idempotent, like DELETE: the postcondition is "this owner no longer holds the lock".
    /// <see cref="LockStatus.Success"/> and <see cref="LockStatus.LockDoesNotExist"/> both satisfy that
    /// postcondition and return <c>true</c> — a lock whose TTL already expired (or was never held) is a
    /// no-op, not a failure. Only a genuine anomaly (<see cref="LockStatus.LockBelongsToOthers"/> or an
    /// infrastructure error) returns <c>false</c>, and is logged as a warning so it can be metered
    /// without faulting the caller's (already-successful) business transition.
    /// </remarks>
    public async Task<bool> ReleaseAsync(
        string resourceKey, string owner, CancellationToken cancellationToken)
    {
        var response = await daprClient.Unlock(
            lockStoreName, resourceKey, owner, cancellationToken);

        switch (response.status)
        {
            case LockStatus.Success:
                logger.ResourceLockReleased(resourceKey, owner);
                return true;

            case LockStatus.LockDoesNotExist:
                // Idempotent: nothing to release (TTL expired or never acquired). Postcondition holds.
                logger.ResourceLockReleaseNoop(resourceKey, owner);
                return true;

            default:
                // LockBelongsToOthers / InternalError: a real anomaly, but releasing is best-effort
                // cleanup — surface it via a warning/metric, never fault the business transition.
                logger.ResourceLockReleaseFailed(resourceKey, owner, response.status.ToString());
                return false;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The Dapr lock API has no native extend, and its Redis component uses <c>SET NX</c>,
    /// which rejects re-acquire attempts even from the same owner. Consequently this call
    /// FAILS while the lock is still held and only "succeeds" after the TTL has already
    /// expired — at which point it is a fresh acquisition race, not an extension (another
    /// owner may win in between). Size <c>ttlSeconds</c> to cover the whole protected
    /// operation instead of relying on extension.
    /// </remarks>
    public async Task<bool> ExtendAsync(
        string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken)
    {
        var response = await daprClient.Lock(
            lockStoreName, resourceKey, owner, ttlSeconds, cancellationToken);

        if (response.Success)
        {
            logger.ResourceLockExtended(resourceKey, owner, ttlSeconds);
            return true;
        }

        logger.ResourceLockExtendFailed(resourceKey, owner);
        return false;
    }
}
