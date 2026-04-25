using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution.PostCommit;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.Execution.PostCommit;

/// <summary>
/// Distributed cache implementation of post-commit idempotency store.
/// Uses IDistributedCacheService for tracking job execution across instances.
/// Uses IDistributedLockService to ensure atomic check-and-set operations.
/// </summary>
public sealed class DistributedCacheIdempotencyStore(
    IDistributedCacheService cache,
    IDistributedLockService lockService,
    ILogger<DistributedCacheIdempotencyStore> logger) : IPostCommitIdempotencyStore
{
    private const string ComponentType = "idempotency";
    private const string KeyPrefix = "postcommit:idempotency:";
    private const string LockKeyPrefix = "postcommit:idempotency:lock:";
    private const string StatusPending = "pending";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";

    /// <summary>
    /// Lock expiry for atomic check-and-set operations.
    /// </summary>
    private const int LockExpiryInSeconds = 30;

    /// <summary>
    /// Default TTL for idempotency entries.
    /// Jobs older than this are considered safe to retry.
    /// </summary>
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public async Task<Result<bool>> TryBeginAsync(string key, CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(key);
        var lockKey = GetLockKey(key);

        var shouldExecute = false;
        string? existingStatus = null;

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, cacheKey, ComponentType);

        try
        {
            var lockAcquired = await lockService.ExecuteWithLockAsync(
                lockKey,
                async () =>
                {
                    var existing = await cache.GetAsync<string>(cacheKey, cancellationToken);
                    if (existing is not null)
                    {
                        CacheActivityHelper.SetCacheHit(activity, true);
                        existingStatus = existing;
                        shouldExecute = false;
                        return;
                    }

                    CacheActivityHelper.SetCacheHit(activity, false);

                    await cache.SetAsync(
                        cacheKey,
                        StatusPending,
                        new DistributedCacheEntryOptions
                        {
                            AbsoluteExpiration = DateTimeOffset.UtcNow.Add(DefaultTtl)
                        },
                        cancellationToken);

                    shouldExecute = true;
                },
                LockExpiryInSeconds,
                cancellationToken);

            if (!lockAcquired)
            {
                logger.LogDebug("Idempotency lock not acquired for key {Key}, skipping", key);
                return Result<bool>.Ok(false);
            }

            if (!shouldExecute)
            {
                logger.LogDebug(
                    "Idempotency key {Key} already exists with status {Status}, skipping",
                    key,
                    existingStatus);
                return Result<bool>.Ok(false);
            }

            logger.LogDebug("Idempotency key {Key} registered as pending", key);
            return Result<bool>.Ok(true);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.LogError(ex, "Failed to check/set idempotency key {Key}", key);
            return Result<bool>.Fail(Error.Failure(
                "IDEMPOTENCY_STORE_ERROR",
                $"Failed to access idempotency store: {ex.Message}"));
        }
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(string key, CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(key);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, cacheKey, ComponentType);

        try
        {
            await cache.SetAsync(
                cacheKey,
                StatusCompleted,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.Add(DefaultTtl)
                },
                cancellationToken);

            logger.LogDebug("Idempotency key {Key} marked as completed", key);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.LogWarning(ex, "Failed to mark idempotency key {Key} as completed", key);
        }
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(string key, string errorCode, string? errorMessage, CancellationToken cancellationToken)
    {
        var cacheKey = GetCacheKey(key);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, cacheKey, ComponentType);

        try
        {
            var value = $"{StatusFailed}:{errorCode}:{errorMessage ?? ""}";
            await cache.SetAsync(
                cacheKey,
                value,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.Add(DefaultTtl)
                },
                cancellationToken);

            logger.LogDebug("Idempotency key {Key} marked as failed: {ErrorCode}", key, errorCode);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.LogWarning(ex, "Failed to mark idempotency key {Key} as failed", key);
        }
    }

    private static string GetCacheKey(string key) => $"{KeyPrefix}{key}";

    private static string GetLockKey(string key) => $"{LockKeyPrefix}{key}";
}

