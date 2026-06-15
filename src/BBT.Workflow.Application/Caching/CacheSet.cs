using BBT.Aether.DistributedCache;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Redis-first cache implementation with DB fallback. No in-memory snapshot.
/// All pods share the same Redis instance, so writes are immediately visible cluster-wide.
/// </summary>
/// <typeparam name="T">The type of entity to cache</typeparam>
public class CacheSet<T>(
    IDistributedCacheService distributedCache,
    ICacheBackend<T> backend,
    ILogger<CacheSet<T>> logger)
    : ICacheSet<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    private static readonly string ComponentKeyName = T.ComponentTypeKey;
    private static readonly TimeSpan FullVersionTtl = TimeSpan.FromMinutes(30);

    public Type EntityType => typeof(T);

    /// <inheritdoc />
    public async Task<Result<T>> GetByVersionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        if (InstanceDataVersionComparer.IsRequestingLatest(version))
            return await GetLatestAsync(domain, key, cancellationToken);

        if (InstanceDataVersionComparer.IsFullVersion(version))
            return await GetFullVersionAsync(domain, key, version!, cancellationToken);

        return await GetArtifactVersionAsync(domain, key, version!, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<T>> GetLatestByNameAsync(
        string domain,
        string name,
        CancellationToken cancellationToken = default)
    {
        return await GetLatestAsync(domain, name, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default)
    {
        if (entity is null)
            return Result.Fail(CacheErrors.EntityCannotBeNull());

        var domain = entity.Domain;
        var key = entity.Key;
        var fullVersion = entity.Version;
        var artifactVersion = InstanceDataVersionComparer.GetArtifactVersion(fullVersion);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, CreateFullKey(domain, key, fullVersion), ComponentKeyName);

        try
        {
            var envelope = CreateEnvelope(entity);

            // 1) Write the full version body (short TTL -- rare access pattern)
            await distributedCache.SetAsync(
                CreateFullKey(domain, key, fullVersion),
                envelope,
                new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.Add(FullVersionTtl) },
                cancellationToken);

            // 2) Overwrite the latest key with the new entity (no TTL)
            await distributedCache.SetAsync(
                CreateLatestKey(domain, key),
                envelope,
                cancellationToken: cancellationToken);

            // 3) Overwrite the artifact key with the new entity (no TTL)
            if (!string.IsNullOrEmpty(artifactVersion))
            {
                await distributedCache.SetAsync(
                    CreateArtifactKey(domain, key, artifactVersion),
                    envelope,
                    cancellationToken: cancellationToken);
            }
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.LogError(ex, "Error writing to distributed cache for {Domain}/{Key}@{Version}", domain, key, fullVersion);
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task<Result> InvalidateAsync(string domain, string key, string version, CancellationToken cancellationToken = default)
    {
        var artifactVersion = InstanceDataVersionComparer.GetArtifactVersion(version);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationRemove, CreateFullKey(domain, key, version), ComponentKeyName);

        try
        {
            await distributedCache.RemoveAsync(CreateLatestKey(domain, key), cancellationToken);

            if (!string.IsNullOrEmpty(artifactVersion))
                await distributedCache.RemoveAsync(CreateArtifactKey(domain, key, artifactVersion), cancellationToken);

            if (InstanceDataVersionComparer.IsFullVersion(version))
                await distributedCache.RemoveAsync(CreateFullKey(domain, key, version), cancellationToken);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.LogError(ex, "Error removing from distributed cache for {Domain}/{Key}@{Version}", domain, key, version);
        }

        return Result.Ok();
    }

    public void Dispose()
    {
    }

    // ────────────────────────────────────────────────────────────────────
    // Private: read paths
    // ────────────────────────────────────────────────────────────────────

    private async Task<Result<T>> GetLatestAsync(string domain, string key, CancellationToken cancellationToken)
    {
        var redisKey = CreateLatestKey(domain, key);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, redisKey, ComponentKeyName);

        // 1) Try Redis
        var entity = await TryGetFromRedisAsync(redisKey, activity, cancellationToken);
        if (entity is not null)
            return Result<T>.Ok(entity);

        CacheActivityHelper.SetCacheHit(activity, false);

        // 2) Fallback to DB: load all versions for the key, find latest
        var allResult = await backend.LoadAllByKeyAsync(domain, key, cancellationToken);
        if (!allResult.IsSuccess || allResult.Value!.Count == 0)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, null));

        var allVersions = allResult.Value!;
        var latestVersion = InstanceDataVersionComparer.FindBestMatch(
            allVersions.Select(e => e.Version), null);

        var latest = allVersions.FirstOrDefault(e =>
            string.Equals(e.Version, latestVersion, StringComparison.OrdinalIgnoreCase));

        if (latest is null)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, null));

        // Populate Redis for future reads
        _ = PopulateCacheAsync(latest, cancellationToken);

        return Result<T>.Ok(latest);
    }

    private async Task<Result<T>> GetArtifactVersionAsync(string domain, string key, string artifactVersion, CancellationToken cancellationToken)
    {
        var redisKey = CreateArtifactKey(domain, key, artifactVersion);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, redisKey, ComponentKeyName);

        // 1) Try Redis
        var entity = await TryGetFromRedisAsync(redisKey, activity, cancellationToken);
        if (entity is not null)
            return Result<T>.Ok(entity);

        CacheActivityHelper.SetCacheHit(activity, false);

        // 2) Fallback to DB: load all versions for the key, find best match for artifact
        var allResult = await backend.LoadAllByKeyAsync(domain, key, cancellationToken);
        if (!allResult.IsSuccess || allResult.Value!.Count == 0)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, artifactVersion));

        var allVersions = allResult.Value!;
        var bestMatch = InstanceDataVersionComparer.FindBestMatch(
            allVersions.Select(e => e.Version), artifactVersion);

        if (string.IsNullOrEmpty(bestMatch))
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, artifactVersion));

        var matched = allVersions.FirstOrDefault(e =>
            string.Equals(e.Version, bestMatch, StringComparison.OrdinalIgnoreCase));

        if (matched is null)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, artifactVersion));

        // Populate artifact key in Redis
        try
        {
            await distributedCache.SetAsync(redisKey, CreateEnvelope(matched), cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error writing artifact cache for {Domain}/{Key}@{Version}", domain, key, artifactVersion);
        }

        return Result<T>.Ok(matched);
    }

    private async Task<Result<T>> GetFullVersionAsync(string domain, string key, string fullVersion, CancellationToken cancellationToken)
    {
        var redisKey = CreateFullKey(domain, key, fullVersion);

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, redisKey, ComponentKeyName);

        // 1) Try Redis
        var entity = await TryGetFromRedisAsync(redisKey, activity, cancellationToken);
        if (entity is not null)
            return Result<T>.Ok(entity);

        CacheActivityHelper.SetCacheHit(activity, false);

        // 2) Fallback to DB: load specific version
        var dbResult = await backend.LoadAsync(domain, key, fullVersion, cancellationToken);
        if (!dbResult.IsSuccess)
            return dbResult;

        var loaded = dbResult.Value!;

        // Populate full version key in Redis with TTL
        try
        {
            await distributedCache.SetAsync(
                redisKey,
                CreateEnvelope(loaded),
                new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.Add(FullVersionTtl) },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error writing full version cache for {Domain}/{Key}@{Version}", domain, key, fullVersion);
        }

        return Result<T>.Ok(loaded);
    }

    // ────────────────────────────────────────────────────────────────────
    // Private: helpers
    // ────────────────────────────────────────────────────────────────────

    private async Task<T?> TryGetFromRedisAsync(
        string redisKey,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await distributedCache.GetAsync<CacheEnvelope<T>>(redisKey, cancellationToken);
            if (envelope?.Entity is not null)
            {
                CacheActivityHelper.SetCacheHit(activity, true);
                HydrateReference(envelope);
                return envelope.Entity;
            }
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            logger.LogError(ex, "Error reading from distributed cache key {RedisKey}", redisKey);
        }

        return null;
    }

    private static void HydrateReference(CacheEnvelope<T> envelope)
    {
        var entity = envelope.Entity!;

        if (!string.IsNullOrEmpty(entity.Domain) &&
            !string.IsNullOrEmpty(entity.Key) &&
            !string.IsNullOrEmpty(entity.Version))
            return;

        entity.SetReference(new Reference(
            envelope.Key,
            envelope.Domain,
            envelope.Flow,
            envelope.Version));
    }

    private static CacheEnvelope<T> CreateEnvelope(T entity)
    {
        return new CacheEnvelope<T>
        {
            Domain = entity.Domain,
            Key = entity.Key,
            Version = entity.Version,
            Flow = entity.ComponentKey,
            Entity = entity
        };
    }

    private async Task PopulateCacheAsync(T entity, CancellationToken cancellationToken)
    {
        try
        {
            await SetAsync(entity, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to populate cache after DB load for {Domain}/{Key}@{Version}",
                entity.Domain, entity.Key, entity.Version);
        }
    }

    private static string CreateLatestKey(string domain, string key)
        => $"{ComponentKeyName}:{domain}:{key}:latest";

    private static string CreateArtifactKey(string domain, string key, string artifactVersion)
        => $"{ComponentKeyName}:{domain}:{key}:artifact:{artifactVersion}";

    private static string CreateFullKey(string domain, string key, string fullVersion)
        => $"{ComponentKeyName}:{domain}:{key}:full:{fullVersion}";
}
