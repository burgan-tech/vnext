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
    public async Task<bool> ReleaseAsync(
        string resourceKey, string owner, CancellationToken cancellationToken)
    {
        var response = await daprClient.Unlock(
            lockStoreName, resourceKey, owner, cancellationToken);

        var success = response.status == LockStatus.Success;

        if (success)
        {
            logger.ResourceLockReleased(resourceKey, owner);
        }
        else
        {
            logger.ResourceLockReleaseFailed(resourceKey, owner, response.status.ToString());
        }

        return success;
    }

    /// <inheritdoc />
    public async Task<bool> ExtendAsync(
        string resourceKey, string owner, int ttlSeconds, CancellationToken cancellationToken)
    {
        // Dapr lock API does not have a native extend; re-acquire with the same owner.
        // Redis-backed lock components allow the same owner to refresh the TTL.
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
