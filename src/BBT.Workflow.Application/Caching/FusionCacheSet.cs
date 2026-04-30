using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZiggyCreatures.Caching.Fusion;

namespace BBT.Workflow.Caching;

/// <summary>
/// FusionCache-backed implementation of <see cref="ICacheSet{T}"/>.
/// Replaces the bespoke L1 (CacheSnapshot/CAS) + L2 (IDistributedCacheService) cascade with
/// FusionCache's hybrid memory+distributed cache. Stampede protection, fail-safe stale reads,
/// and soft/hard timeouts are delegated to FusionCache. Cross-pod synchronization relies on
/// Dapr broadcast (<c>ComponentPublishedEvent</c>) rather than a FusionCache backplane, because
/// component data is immutable — key-based invalidation adds no value when keys never change.
/// Domain-specific concerns kept in this class:
///   - Smart version matching (artifact / partial / "latest") via <see cref="InstanceDataVersionComparer"/>
///   - Cross-pod version index via <see cref="IComponentVersionIndex"/> (Redis-backed)
///   - Local "domain:key -> versions" index used by enumeration-style queries that FusionCache
///     cannot serve from its memory cache (no key iteration API).
/// </summary>
public class FusionCacheSet<T>(
    IFusionCache entityCache,
    IFusionCache vidxCache,
    ICacheBackend<T> backend,
    IComponentVersionIndex versionIndex,
    ILogger<FusionCacheSet<T>> logger,
    IOptions<CacheWarmupOptions>? warmupOptions = null)
    : ICacheSet<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    private readonly CacheWarmupOptions _warmupOptions = warmupOptions?.Value ?? new CacheWarmupOptions();

    // Logical component-type discriminator ("sys-flows", "sys-tasks", ...). Used both as cache-key
    // prefix and as the vidx component-key so FusionCache and the version index stay aligned.
    private static readonly string ComponentKeyName = T.ComponentTypeKey;

    // domain:key -> sorted set of versions known to live under this CacheSet. Maintained on every
    // Set/LoadAll/Invalidate. FusionCache does not expose key enumeration, so this index is
    // the source of truth for GetAllByDomain/GetAllByName-style queries. Memory cost is O(keys),
    // not O(entries) - we never store T here.
    private readonly ConcurrentDictionary<string, ImmutableSortedSet<string>> _domainIndex = new();

    public Type EntityType => typeof(T);

    // ----------------
    // Public API
    // ----------------

    public async Task<Result<T>> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var parsed = TryParseCacheKey(cacheKey);
        if (parsed is null)
        {
            // Don't even consult FusionCache for malformed keys - they cannot map back to a backend load.
            logger.LogWarning("Invalid cache key format: {CacheKey}", cacheKey);
            return Result<T>.Fail(CacheErrors.InvalidCacheKeyFormat(cacheKey));
        }

        var (domain, key, version) = parsed.Value;

        // Capture the DB-fallback Result so the caller still sees its NotFound/Validation error
        // when the factory ultimately returns null. FusionCache only surfaces the value (or default)
        // from GetOrSetAsync, so the Result is propagated out-of-band via this local.
        Result<T>? loadResult = null;

        var entity = await entityCache.GetOrSetAsync<T?>(
            cacheKey,
            async (ctx, ct) =>
            {
                var dbResult = await backend.LoadAsync(domain, key, version, ct).ConfigureAwait(false);
                loadResult = dbResult;

                if (!dbResult.IsSuccess || dbResult.Value is null)
                {
                    // Negative lookup: don't poison L1/L2 with a tombstone. The next request retries.
                    ctx.Options.Duration = TimeSpan.Zero;
                    ctx.Options.SkipMemoryCacheWrite = true;
                    ctx.Options.SkipDistributedCacheWrite = true;
                    return null;
                }

                var v = dbResult.Value;
                EnsureReferenceIsSet(v, cacheKey);
                TrackVersion(v);

                if (!string.IsNullOrEmpty(v.Domain) && !string.IsNullOrEmpty(v.Key) && !string.IsNullOrEmpty(v.Version))
                {
                    _ = versionIndex.AddVersionAsync(ComponentKeyName, v.Domain, v.Key, v.Version, ct);
                    _ = RefreshVidxCacheAsync(v.Domain, v.Key, ct);
                }

                return v;
            },
            token: cancellationToken).ConfigureAwait(false);

        if (entity is not null)
        {
            EnsureReferenceIsSet(entity, cacheKey);
            TrackVersion(entity);
            return Result<T>.Ok(entity);
        }

        return loadResult ?? Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
    }

    public Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default)
        => SetAsyncCore(entity, awaitDistributedCache: false, cancellationToken);

    public Task<Result> SetAsync(T entity, bool awaitDistributedCache, CancellationToken cancellationToken = default)
        => SetAsyncCore(entity, awaitDistributedCache, cancellationToken);

    private async Task<Result> SetAsyncCore(T entity, bool awaitDistributedCache, CancellationToken cancellationToken)
    {
        if (entity is null)
            return Result.Fail(CacheErrors.EntityCannotBeNull());

        var cacheKey = CreateCacheKey(entity);

        if (awaitDistributedCache)
        {
            await entityCache.SetAsync(
                cacheKey,
                entity,
                opts => { opts.AllowBackgroundDistributedCacheOperations = false; },
                token: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await entityCache.SetAsync(cacheKey, entity, token: cancellationToken).ConfigureAwait(false);
        }

        TrackVersion(entity);

        if (!string.IsNullOrEmpty(entity.Domain) && !string.IsNullOrEmpty(entity.Key) && !string.IsNullOrEmpty(entity.Version))
        {
            _ = versionIndex.AddVersionAsync(
                ComponentKeyName,
                entity.Domain,
                entity.Key,
                entity.Version,
                cancellationToken);

            _ = RefreshVidxCacheAsync(entity.Domain, entity.Key, cancellationToken);
        }

        return Result.Ok();
    }

    public async Task<Result<List<T>>> GetAllByNameAsync(
        string domain,
        string name,
        CancellationToken cancellationToken = default)
    {
        var indexKey = CreateIndexKey(domain, name);
        if (!_domainIndex.TryGetValue(indexKey, out var versions) || versions.Count == 0)
            return Result<List<T>>.Ok(new List<T>());

        var result = new List<T>(versions.Count);
        foreach (var version in versions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entity = await entityCache.GetOrDefaultAsync<T?>(
                CreateCacheKey(domain, name, version),
                default,
                token: cancellationToken).ConfigureAwait(false);

            if (entity is not null)
                result.Add(entity);
        }

        return Result<List<T>>.Ok(result);
    }

    public async Task<Result<List<T>>> GetAllByDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        // Group all "{domain}:{key}" entries in the local index that belong to this domain,
        // then resolve the latest version per key from FusionCache.
        var prefix = domain + ":";
        var grouped = _domainIndex
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new List<T>(grouped.Count);
        foreach (var kvp in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var key = kvp.Key.Substring(prefix.Length);
            var versions = kvp.Value;
            if (versions.Count == 0) continue;

            var latest = versions
                .OrderByDescending(v => v, new SemVersionComparer())
                .FirstOrDefault();

            if (string.IsNullOrEmpty(latest)) continue;

            var entity = await entityCache.GetOrDefaultAsync<T?>(
                CreateCacheKey(domain, key, latest),
                default,
                token: cancellationToken).ConfigureAwait(false);

            if (entity is not null)
                result.Add(entity);
        }

        return Result<List<T>>.Ok(result);
    }

    public async Task<Result<T>> GetLatestByNameAsync(
        string domain,
        string name,
        CancellationToken cancellationToken = default)
    {
        var indexKey = CreateIndexKey(domain, name);

        string? localLatest = null;
        if (_domainIndex.TryGetValue(indexKey, out var versions) && versions.Count > 0)
        {
            localLatest = versions
                .OrderByDescending(v => v, new SemVersionComparer())
                .FirstOrDefault();
        }

        // Only consult Redis vidx when the local index has no answer. Warm pods already
        // have the full version set in _domainIndex, so skipping the Redis round-trip
        // keeps the "hot" path entirely in-process.
        string? indexLatest = null;
        if (string.IsNullOrEmpty(localLatest))
        {
            var redisVersions = await GetCachedVersionsAsync(domain, name, cancellationToken).ConfigureAwait(false);
            indexLatest = redisVersions is { Count: > 0 }
                ? InstanceDataVersionComparer.FindBestMatch(redisVersions, null)
                : null;
        }

        var authoritativeLatest = ChooseHigher(indexLatest, localLatest);

        if (!string.IsNullOrEmpty(authoritativeLatest))
        {
            var resolvedKey = CreateCacheKey(domain, name, authoritativeLatest);
            return await GetAsync(resolvedKey, cancellationToken).ConfigureAwait(false);
        }

        // Fallback: load all versions from backend, populate cache, smart-match.
        var allResult = await backend.LoadAllByKeyAsync(domain, name, cancellationToken).ConfigureAwait(false);
        if (!allResult.IsSuccess)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, name, null));

        var allVersions = allResult.Value!;
        if (allVersions.Count == 0)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, name, null));

        await SetManyAsync(allVersions, cancellationToken).ConfigureAwait(false);

        var foundLatestVersion = InstanceDataVersionComparer.FindBestMatch(
            allVersions.Select(e => e.Version), null);

        var latest = allVersions.FirstOrDefault(e =>
            string.Equals(e.Version, foundLatestVersion, StringComparison.OrdinalIgnoreCase));

        return latest is not null
            ? Result<T>.Ok(latest)
            : Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, name, null));
    }

    public async Task<Result<T>> GetByVersionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        if (InstanceDataVersionComparer.IsRequestingLatest(version))
            return await GetLatestByNameAsync(domain, key, cancellationToken).ConfigureAwait(false);

        if (InstanceDataVersionComparer.IsFullVersion(version))
        {
            var cacheKey = CreateCacheKey(domain, key, version);
            return await GetAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        }

        // Partial / artifact version: smart-match across local + Redis indices.
        var indexKey = CreateIndexKey(domain, key);

        string? localBest = null;
        if (_domainIndex.TryGetValue(indexKey, out var versions) && versions.Count > 0)
            localBest = InstanceDataVersionComparer.FindBestMatch(versions, version);

        string? indexBest = null;
        if (string.IsNullOrEmpty(localBest))
        {
            var redisVersions = await GetCachedVersionsAsync(domain, key, cancellationToken).ConfigureAwait(false);
            indexBest = redisVersions is { Count: > 0 }
                ? InstanceDataVersionComparer.FindBestMatch(redisVersions, version)
                : null;
        }

        var authoritative = ChooseHigher(indexBest, localBest);

        if (!string.IsNullOrEmpty(authoritative))
        {
            var resolvedKey = CreateCacheKey(domain, key, authoritative);
            return await GetAsync(resolvedKey, cancellationToken).ConfigureAwait(false);
        }

        // Not in any index: load all from backend then smart-match.
        var allResult = await backend.LoadAllByKeyAsync(domain, key, cancellationToken).ConfigureAwait(false);
        if (!allResult.IsSuccess)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));

        var allVersions = allResult.Value!;
        if (allVersions.Count == 0)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));

        await SetManyAsync(allVersions, cancellationToken).ConfigureAwait(false);

        var candidateVersions = allVersions.Select(e => e.Version).ToList();
        var dbBestMatch = InstanceDataVersionComparer.FindBestMatch(candidateVersions, version);

        if (string.IsNullOrEmpty(dbBestMatch))
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));

        var matched = allVersions.FirstOrDefault(e =>
            string.Equals(e.Version, dbBestMatch, StringComparison.OrdinalIgnoreCase));

        return matched is not null
            ? Result<T>.Ok(matched)
            : Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));
    }

    public async Task<Result> InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        await entityCache.RemoveAsync(cacheKey, token: cancellationToken).ConfigureAwait(false);

        var parsed = TryParseCacheKey(cacheKey);
        if (parsed is not null && !string.IsNullOrEmpty(parsed.Value.Version))
        {
            UntrackVersion(parsed.Value.Domain, parsed.Value.Key, parsed.Value.Version!);

            _ = versionIndex.RemoveVersionAsync(
                ComponentKeyName,
                parsed.Value.Domain,
                parsed.Value.Key,
                parsed.Value.Version!,
                cancellationToken);

            _ = RefreshVidxCacheAsync(parsed.Value.Domain, parsed.Value.Key, cancellationToken);
        }

        return Result.Ok();
    }

    public async Task LoadAllAsync(object data, CancellationToken cancellationToken = default)
    {
        if (data is not IEnumerable<T> entities)
            throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

        var list = entities as IList<T> ?? entities.ToList();

        // Reset local index then write through to memory only (warmup ⇒ no L2 fan-out).
        _domainIndex.Clear();

        foreach (var entity in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = CreateCacheKey(entity);
            EnsureReferenceIsSet(entity, cacheKey);

            await entityCache.SetAsync(
                cacheKey,
                entity,
                opts => opts.SetSkipDistributedCache(true, true),
                token: cancellationToken).ConfigureAwait(false);

            TrackVersion(entity);
        }

        logger.LogInformation(
            "FusionCacheSet<{Type}> initialized. Items: {Count}",
            typeof(T).Name, list.Count);
    }

    public async Task LoadAllWithDistributedCacheAsync(object data, CancellationToken cancellationToken = default)
    {
        if (data is not IEnumerable<T> entities)
            throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

        var list = entities as IList<T> ?? entities.ToList();
        _domainIndex.Clear();

        foreach (var entity in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = CreateCacheKey(entity);
            EnsureReferenceIsSet(entity, cacheKey);

            // Default options write to both L1 and L2 (background L2 writes if AllowBackground… is on).
            await entityCache.SetAsync(cacheKey, entity, token: cancellationToken).ConfigureAwait(false);

            TrackVersion(entity);
        }

        logger.LogInformation(
            "FusionCacheSet<{Type}> initialized with distributed cache. Items: {Count}",
            typeof(T).Name, list.Count);
    }

    public async Task MergeAllAsync(object data, CancellationToken cancellationToken = default)
    {
        if (data is not IEnumerable<T> entities)
            throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

        var count = 0;
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cacheKey = CreateCacheKey(entity);
            EnsureReferenceIsSet(entity, cacheKey);

            await entityCache.SetAsync(
                cacheKey,
                entity,
                opts => opts.SetSkipDistributedCache(true, true),
                token: cancellationToken).ConfigureAwait(false);

            TrackVersion(entity);
            count++;
        }

        logger.LogInformation(
            "FusionCacheSet<{Type}> merged {Count} item(s) incrementally.",
            typeof(T).Name, count);
    }

    /// <summary>
    /// FusionCache evicts via MemoryCache TTL/size limits, so per-set Cleanup is a no-op.
    /// Method retained for ICacheSet contract compatibility; returns 0.
    /// </summary>
    public int Cleanup(
        TimeSpan? ttl = null,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        return 0;
    }

    public async Task LoadFromDistributedCacheAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken = default)
    {
        var keys = cacheKeys as IReadOnlyCollection<string> ?? cacheKeys.ToArray();
        if (keys.Count == 0) return;

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, _warmupOptions.MaxConcurrencyPerCacheSet)
        };

        await Parallel.ForEachAsync(keys, options, async (cacheKey, ct) =>
        {
            try
            {
                // GetOrSetAsync triggers FusionCache's L1→L2→factory cascade with stampede protection,
                // so a publish broadcast on N pods produces at most one DB load per key cluster-wide.
                _ = await GetAsync(cacheKey, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to warm cache for key {CacheKey}", cacheKey);
            }
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // FusionCache instance lifetime is owned by DI; nothing to dispose here.
    }

    // ----------------
    // Helpers
    // ----------------

    private async Task SetManyAsync(IEnumerable<T> entities, CancellationToken ct)
    {
        foreach (var entity in entities)
        {
            ct.ThrowIfCancellationRequested();
            await SetAsync(entity, ct).ConfigureAwait(false);
        }
    }

    private void TrackVersion(T entity)
    {
        if (string.IsNullOrEmpty(entity.Domain) ||
            string.IsNullOrEmpty(entity.Key) ||
            string.IsNullOrEmpty(entity.Version))
            return;

        var indexKey = CreateIndexKey(entity.Domain, entity.Key);
        var version = entity.Version;

        _domainIndex.AddOrUpdate(
            indexKey,
            _ => ImmutableSortedSet.Create(new SemVersionComparer(), version),
            (_, existing) => existing.Add(version));
    }

    private void UntrackVersion(string domain, string key, string version)
    {
        var indexKey = CreateIndexKey(domain, key);

        _domainIndex.AddOrUpdate(
            indexKey,
            _ => ImmutableSortedSet<string>.Empty.WithComparer(new SemVersionComparer()),
            (_, existing) => existing.Remove(version));

        if (_domainIndex.TryGetValue(indexKey, out var current) && current.Count == 0)
            _domainIndex.TryRemove(indexKey, out _);
    }

    /// <summary>
    /// Routes Redis vidx lookups through the dedicated "workflow.vidx" FusionCache (memory-only,
    /// 60s TTL, fail-safe 5min). FusionCache's per-key factory lock collapses concurrent "latest"
    /// resolutions for the same domain:key into a single Redis roundtrip; fail-safe lets us serve
    /// the last-known version set when Redis is briefly unreachable.
    /// </summary>
    private async Task<SortedSet<string>?> GetCachedVersionsAsync(
        string domain, string key, CancellationToken cancellationToken)
    {
        var vidxKey = CreateVidxCacheKey(domain, key);

        return await vidxCache.GetOrSetAsync<SortedSet<string>?>(
            vidxKey,
            async (_, ct) => await versionIndex.GetVersionsAsync(
                ComponentKeyName, domain, key, ct).ConfigureAwait(false),
            token: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuilds the vidx cache entry from the local <c>_domainIndex</c> so that subsequent
    /// <see cref="GetCachedVersionsAsync"/> calls on any code path (including cold pods
    /// that lack a populated <c>_domainIndex</c>) can resolve versions without an extra
    /// Redis round-trip. Called after a version is added to the Redis vidx.
    /// </summary>
    private async Task RefreshVidxCacheAsync(string domain, string key, CancellationToken cancellationToken)
    {
        var indexKey = CreateIndexKey(domain, key);
        if (!_domainIndex.TryGetValue(indexKey, out var localVersions) || localVersions.Count == 0)
            return;

        var vidxKey = CreateVidxCacheKey(domain, key);
        var snapshot = new SortedSet<string>(localVersions, StringComparer.OrdinalIgnoreCase);
        await vidxCache.SetAsync(vidxKey, (SortedSet<string>?)snapshot, token: cancellationToken).ConfigureAwait(false);
    }

    private static string CreateCacheKey(T entity)
        => CreateCacheKey(entity.Domain, entity.Key, entity.Version);

    private static string CreateCacheKey(string domain, string key, string? version)
        => $"{ComponentKeyName}:{domain}:{key}:{version}";

    private static string CreateIndexKey(string domain, string name)
        => $"{domain}:{name}";

    // vidx cache keys must be globally unique across all FusionCacheSet<T> instances since they
    // share one named FusionCache. Prefix with the component-type discriminator.
    private static string CreateVidxCacheKey(string domain, string name)
        => $"vidx:{ComponentKeyName}:{domain}:{name}";

    private static string? ChooseHigher(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return new SemVersionComparer().Compare(a, b) >= 0 ? a : b;
    }

    private static (string Domain, string Key, string? Version)? TryParseCacheKey(string cacheKey)
    {
        var match = Regex.Match(cacheKey, @"^([^:]+):([^:]+):([^:]+):([^:]+)$");
        if (!match.Success)
            return null;

        return (match.Groups[2].Value, match.Groups[3].Value, match.Groups[4].Value);
    }

    private void EnsureReferenceIsSet(T entity, string cacheKey)
    {
        if (!string.IsNullOrEmpty(entity.Domain) &&
            !string.IsNullOrEmpty(entity.Key) &&
            !string.IsNullOrEmpty(entity.Version))
        {
            return;
        }

        var parsed = TryParseCacheKey(cacheKey);
        if (parsed is null) return;

        var (domain, key, version) = parsed.Value;
        try
        {
            var reference = new Reference(key, domain, entity.ComponentKey, version!);
            entity.SetReference(reference);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error setting reference from cache key: {CacheKey}", cacheKey);
        }
    }
}
