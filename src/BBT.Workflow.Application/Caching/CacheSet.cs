using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BBT.Aether.DistributedCache;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides a thread-safe cache implementation using an immutable snapshot model.
/// Supports local in-memory caching, distributed caching, and database fallback.
/// </summary>
/// <typeparam name="T">The type of entity to cache</typeparam>
public class CacheSet<T>(
    IDistributedCacheService distributedCache,
    ICacheBackend<T> backend,
    IComponentVersionIndex versionIndex,
    ILogger<CacheSet<T>> logger,
    IOptions<CacheWarmupOptions>? warmupOptions = null)
    : ICacheSet<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    private readonly ILogger _logger = logger;
    private readonly CacheWarmupOptions _warmupOptions = warmupOptions?.Value ?? new CacheWarmupOptions();

    // Logical component-type discriminator (e.g. "sys-flows", "sys-tasks") sourced from
    // the entity's static abstract ComponentTypeKey. Used as the prefix for both the body
    // cache key (CreateCacheKey) and the Redis version index key (vidx:{ComponentKeyName}:...)
    // so the two systems stay aligned across pods.
    private static readonly string ComponentKeyName = T.ComponentTypeKey;

    // Immutable snapshot holding all local cache state
    private CacheSnapshot<T> _snapshot = new(
        ImmutableDictionary<string, CacheItem<T>>.Empty,
        ImmutableDictionary<string, SortedSet<string>>.Empty);

    // Short-TTL local cache for Redis version index (vidx) results.
    // Prevents redundant Redis roundtrips during execution; cross-pod freshness
    // is guaranteed by the Dapr ComponentPublishedEvent warm-up path.
    private readonly ConcurrentDictionary<string, (SortedSet<string>? Versions, DateTime FetchedAt)> _vidxCache = new();
    private static readonly TimeSpan VidxCacheTtl = TimeSpan.FromSeconds(60);

    // Default cache configuration
    private static readonly TimeSpan DefaultItemTtl = TimeSpan.FromHours(12);
    private const int DefaultMaxItems = 10_000;

    public Type EntityType => typeof(T);

    // ----------------
    // Public API
    // ----------------

    public async Task<Result<T>> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        // 1) Lock-free snapshot read
        var snap = _snapshot;
        if (snap.Entries.TryGetValue(cacheKey, out var item))
        {
            item.UpdateAccess();
            return Result<T>.Ok(item.Value);
        }

        // 2) Distributed cache - network errors will throw (expected per Railway Pattern)
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, cacheKey, ComponentKeyName);

        try
        {
            var fromDistributed = await distributedCache.GetAsync<T>(cacheKey, cancellationToken);
            if (fromDistributed is not null)
            {
                CacheActivityHelper.SetCacheHit(activity, true);
                EnsureReferenceIsSet(fromDistributed, cacheKey);
                _ = UpsertLocalAsync(fromDistributed, cancellationToken);
                return Result<T>.Ok(fromDistributed);
            }
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            _logger.LogError(ex, "Error reading from distributed cache for {CacheKey}", cacheKey);
        }

        CacheActivityHelper.SetCacheHit(activity, false);

        // 3) Database backend fallback
        var parsed = TryParseCacheKey(cacheKey);
        if (parsed is null)
        {
            _logger.LogWarning("Invalid cache key format: {CacheKey}", cacheKey);
            return Result<T>.Fail(CacheErrors.InvalidCacheKeyFormat(cacheKey));
        }

        var (domain, key, version) = parsed.Value;
        var fromDbResult = await backend.LoadAsync(domain, key, version, cancellationToken);
        
        if (!fromDbResult.IsSuccess)
        {
            return fromDbResult;
        }

        var fromDb = fromDbResult.Value!;
        EnsureReferenceIsSet(fromDb, cacheKey);
        _ = SetAsync(fromDb, cancellationToken);

        return Result<T>.Ok(fromDb);
    }

    public async Task<Result> SetAsync(T entity, CancellationToken cancellationToken = default)
    {
        if (entity is null)
        {
            return Result.Fail(CacheErrors.EntityCannotBeNull());
        }

        var cacheKey = CreateCacheKey(entity);

        // 1) Update local cache via snapshot
        SnapshotUpsert(cacheKey, entity);

        // 2) Write to distributed cache - network errors will throw (expected per Railway Pattern)
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, cacheKey, ComponentKeyName);

        try
        {
            await distributedCache.SetAsync(cacheKey, entity, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            _logger.LogError(ex, "Error writing to distributed cache for {CacheKey}", cacheKey);
        }

        // 3) Update version index in Redis (best-effort, errors are swallowed by the impl).
        // Allows partial/"latest" lookups on other pods to resolve via Redis without a DB scan.
        if (!string.IsNullOrEmpty(entity.Domain) && !string.IsNullOrEmpty(entity.Key) && !string.IsNullOrEmpty(entity.Version))
        {
            _ = versionIndex.AddVersionAsync(
                ComponentKeyName,
                entity.Domain,
                entity.Key,
                entity.Version,
                cancellationToken);

            _vidxCache.TryRemove(CreateIndexKey(entity.Domain, entity.Key), out _);
        }

        return Result.Ok();
    }

    public Task<Result<List<T>>> GetAllByNameAsync(
        string domain,
        string name,
        CancellationToken cancellationToken = default)
    {
        var snap = _snapshot;
        var indexKey = CreateIndexKey(domain, name);

        if (!snap.Index.TryGetValue(indexKey, out var versions) || versions.Count == 0)
            return Task.FromResult(Result<List<T>>.Ok(new List<T>()));

        var result = new List<T>();

        // Filter snapshot entries by domain and key (name), then match versions
        foreach (var entry in snap.Entries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entity = entry.Value;
            if (string.Equals(entity.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(entity.Key, name, StringComparison.OrdinalIgnoreCase) &&
                versions.Contains(entity.Version))
            {
                entry.UpdateAccess();
                result.Add(entity);
            }
        }

        return Task.FromResult(Result<List<T>>.Ok(result));
    }

    public Task<Result<List<T>>> GetAllByDomainAsync(string domain, CancellationToken cancellationToken = default)
    {
        var snap = _snapshot;

        var grouped = snap.Entries.Values
            .Select(ci => ci.Value)
            .Where(e => string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => e.Key);

        var result = new List<T>();

        foreach (var group in grouped)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var latest = group
                .OrderByDescending(e => e.Version, new SemVersionComparer())
                .FirstOrDefault();

            if (latest is not null)
                result.Add(latest);
        }

        return Task.FromResult(Result<List<T>>.Ok(result));
    }

    public async Task<Result<T>> GetLatestByNameAsync(
        string domain,
        string name,
        CancellationToken cancellationToken = default)
    {
        var snap = _snapshot;
        var indexKey = CreateIndexKey(domain, name);

        string? localLatest = null;

        if (snap.Index.TryGetValue(indexKey, out var versions) && versions.Count > 0)
        {
            localLatest = versions
                .OrderByDescending(v => v, new SemVersionComparer())
                .FirstOrDefault();
        }

        // Consult the Redis version index so a publish on another pod becomes visible
        // without waiting for ReInitialize. Results are locally cached for a short TTL
        // to avoid redundant Redis roundtrips during a single execution burst.
        var redisVersions = await GetCachedVersionsAsync(domain, name, cancellationToken);

        var indexLatest = redisVersions is { Count: > 0 }
            ? InstanceDataVersionComparer.FindBestMatch(redisVersions, null)
            : null;

        var authoritativeLatest = ChooseHigher(indexLatest, localLatest);

        if (!string.IsNullOrEmpty(authoritativeLatest))
        {
            // Fast path: the authoritative version is already in the local snapshot.
            // Direct O(1) dictionary lookup using the canonical cache-key format.
            if (string.Equals(authoritativeLatest, localLatest, StringComparison.OrdinalIgnoreCase) &&
                snap.Entries.TryGetValue(CreateCacheKey(domain, name, authoritativeLatest), out var entry))
            {
                entry.UpdateAccess();
                return Result<T>.Ok(entry.Value);
            }

            // Resolved via Redis index (or local entry missing) - load body via the
            // standard cacheKey path (snapshot -> Redis body -> single-version DB).
            var resolvedKey = CreateCacheKey(domain, name, authoritativeLatest);
            return await GetAsync(resolvedKey, cancellationToken);
        }

        // Not in snapshot: load ALL versions for this key from backend
        var allResult = await backend.LoadAllByKeyAsync(domain, name, cancellationToken);

        if (!allResult.IsSuccess)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, name, null));

        var allVersions = allResult.Value!;

        if (allVersions.Count == 0)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, name, null));

        // Cache all versions: SnapshotUpsert runs synchronously (local cache updated immediately),
        // distributed cache writes run asynchronously in the background
        foreach (var entity in allVersions)
            _ = SetAsync(entity, cancellationToken);

        // Find the latest version from the in-memory result set
        var foundLatestVersion = InstanceDataVersionComparer.FindBestMatch(
            allVersions.Select(e => e.Version), null);

        var latest = allVersions.FirstOrDefault(e =>
            string.Equals(e.Version, foundLatestVersion, StringComparison.OrdinalIgnoreCase));

        return latest is not null
            ? Result<T>.Ok(latest)
            : Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, name, null));
    }

    /// <inheritdoc />
    public async Task<Result<T>> GetByVersionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        // 1) If version is null/empty or "latest" → return latest version
        if (InstanceDataVersionComparer.IsRequestingLatest(version))
        {
            return await GetLatestByNameAsync(domain, key, cancellationToken);
        }

        // 2) If full version → try exact match first from local snapshot
        if (InstanceDataVersionComparer.IsFullVersion(version))
        {
            var cacheKey = CreateCacheKey(domain, key, version);
            return await GetAsync(cacheKey, cancellationToken);
        }

        // 3) For artifact or partial version → use smart matching.
        // Consult the Redis vidx (with short-TTL local cache) alongside the local
        // snapshot so a fresh publish on another pod is visible.
        var snap = _snapshot;
        var indexKey = CreateIndexKey(domain, key);

        string? localBest = null;
        if (snap.Index.TryGetValue(indexKey, out var versions) && versions.Count > 0)
        {
            localBest = InstanceDataVersionComparer.FindBestMatch(versions, version);
        }

        var redisVersions = await GetCachedVersionsAsync(domain, key, cancellationToken);

        var indexBest = redisVersions is { Count: > 0 }
            ? InstanceDataVersionComparer.FindBestMatch(redisVersions, version)
            : null;

        var authoritative = ChooseHigher(indexBest, localBest);

        if (!string.IsNullOrEmpty(authoritative))
        {
            // Fast path: snapshot already has the authoritative version.
            // Direct O(1) dictionary lookup using the canonical cache-key format.
            if (string.Equals(authoritative, localBest, StringComparison.OrdinalIgnoreCase) &&
                snap.Entries.TryGetValue(CreateCacheKey(domain, key, authoritative), out var entry))
            {
                entry.UpdateAccess();
                return Result<T>.Ok(entry.Value);
            }

            var resolvedKey = CreateCacheKey(domain, key, authoritative);
            return await GetAsync(resolvedKey, cancellationToken);
        }

        // 4) Not in snapshot: load ALL versions for key from backend, cache them all, then smart match
        var allResult = await backend.LoadAllByKeyAsync(domain, key, cancellationToken);

        if (!allResult.IsSuccess)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));

        var allVersions = allResult.Value!;

        if (allVersions.Count == 0)
            return Result<T>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, version));

        // Cache all versions: SnapshotUpsert runs synchronously (local cache updated immediately),
        // distributed cache writes run asynchronously in the background
        foreach (var entity in allVersions)
            _ = SetAsync(entity, cancellationToken);

        // Smart match from in-memory result set
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
        // Remove from snapshot
        SnapshotRemove(cacheKey);

        // Remove from distributed cache - network errors will throw (expected per Railway Pattern)
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationRemove, cacheKey, ComponentKeyName);

        try
        {
            await distributedCache.RemoveAsync(cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            _logger.LogError(ex, "Error removing from distributed cache for {CacheKey}", cacheKey);
        }

        // Drop the version from the Redis index so other pods don't resolve to a missing body.
        var parsed = TryParseCacheKey(cacheKey);
        if (parsed is not null && !string.IsNullOrEmpty(parsed.Value.Version))
        {
            _ = versionIndex.RemoveVersionAsync(
                ComponentKeyName,
                parsed.Value.Domain,
                parsed.Value.Key,
                parsed.Value.Version!,
                cancellationToken);

            _vidxCache.TryRemove(CreateIndexKey(parsed.Value.Domain, parsed.Value.Key), out _);
        }

        return Result.Ok();
    }

    public Task LoadAllAsync(object data, CancellationToken cancellationToken = default)
    {
        if (data is not IEnumerable<T> entities)
            throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

        var entries = new Dictionary<string, CacheItem<T>>();
        var index = new Dictionary<string, SortedSet<string>>();

        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = CreateCacheKey(entity);
            EnsureReferenceIsSet(entity, cacheKey);

            var item = new CacheItem<T>(entity);
            entries[cacheKey] = item;
            UpdateIndex(index, entity);
        }

        var newSnapshot = new CacheSnapshot<T>(
            entries.ToImmutableDictionary(),
            index.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

        Interlocked.Exchange(ref _snapshot, newSnapshot);

        _logger.LogInformation(
            "CacheSet<{Type}> initialized. Items: {Count}",
            typeof(T).Name, entries.Count);

        return Task.CompletedTask;
    }

    public async Task LoadAllWithDistributedCacheAsync(object data, CancellationToken cancellationToken = default)
    {
        if (data is not IEnumerable<T> entities)
            throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

        var entitiesList = entities.ToList();
        var entries = new Dictionary<string, CacheItem<T>>();
        var index = new Dictionary<string, SortedSet<string>>();

        // First, update in-memory cache
        foreach (var entity in entitiesList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = CreateCacheKey(entity);
            EnsureReferenceIsSet(entity, cacheKey);

            var item = new CacheItem<T>(entity);
            entries[cacheKey] = item;
            UpdateIndex(index, entity);
        }

        var newSnapshot = new CacheSnapshot<T>(
            entries.ToImmutableDictionary(),
            index.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

        Interlocked.Exchange(ref _snapshot, newSnapshot);

        _logger.LogInformation(
            "CacheSet<{Type}> initialized with distributed cache. Items: {Count}",
            typeof(T).Name, entries.Count);

        // Second, write all entities to distributed cache
        var writeErrors = 0;
        foreach (var entity in entitiesList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var cacheKey = CreateCacheKey(entity);
            try
            {
                await distributedCache.SetAsync(cacheKey, entity, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                writeErrors++;
                _logger.LogError(ex, "Error writing to distributed cache for {CacheKey}", cacheKey);
                // Continue with remaining entities even if one fails
            }
        }

        if (writeErrors > 0)
        {
            _logger.LogWarning(
                "CacheSet<{Type}> distributed cache write completed with {ErrorCount} errors out of {TotalCount} items",
                typeof(T).Name, writeErrors, entitiesList.Count);
        }
    }

    public async Task MergeAllAsync(object data, CancellationToken cancellationToken = default)
    {
        if (data is not IEnumerable<T> entities)
            throw new ArgumentException($"Invalid data type for {typeof(T).Name}");

        var count = 0;
        foreach (var entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertLocalAsync(entity, cancellationToken);
            count++;
        }

        _logger.LogInformation(
            "CacheSet<{Type}> merged {Count} item(s) incrementally.",
            typeof(T).Name, count);
    }

    /// <summary>
    /// Performs cleanup of expired and least-used items based on TTL and capacity.
    /// </summary>
    public int Cleanup(
        TimeSpan? ttl = null,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        ttl ??= DefaultItemTtl;
        maxItems ??= DefaultMaxItems;

        var now = DateTime.UtcNow;
        var current = _snapshot;

        var keptEntries = new Dictionary<string, CacheItem<T>>();
        var keptIndex = new Dictionary<string, SortedSet<string>>();

        var removedCount = 0;

        // 1) TTL filter
        foreach (var (key, item) in current.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var age = now - item.CreatedTime;
            var idle = now - item.LastAccessTime;

            if (age > ttl.Value || idle > ttl.Value)
            {
                removedCount++;
                continue;
            }

            keptEntries[key] = item;
            UpdateIndex(keptIndex, item.Value);
        }

        // 2) Capacity filter (if needed)
        if (maxItems > 0 && keptEntries.Count > maxItems.Value)
        {
            var overflow = keptEntries.Count - maxItems.Value;

            var ordered = keptEntries
                .OrderBy(kvp => kvp.Value.AccessCount)
                .ThenBy(kvp => kvp.Value.LastAccessTime)
                .ToList();

            // Remove first 'overflow' items
            for (var i = 0; i < overflow; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (key, _) = ordered[i];
                keptEntries.Remove(key);
                removedCount++;
            }

            // Rebuild index
            keptIndex.Clear();
            foreach (var kvp in keptEntries)
            {
                UpdateIndex(keptIndex, kvp.Value.Value);
            }
        }

        var newSnapshot = new CacheSnapshot<T>(
            keptEntries.ToImmutableDictionary(),
            keptIndex.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

        Interlocked.Exchange(ref _snapshot, newSnapshot);

        if (removedCount > 0)
        {
            _logger.LogInformation(
                "Cleanup completed for CacheSet<{Type}>. Removed={Removed}, Remaining={Remaining}",
                typeof(T).Name, removedCount, keptEntries.Count);
        }

        return removedCount;
    }

    public async Task LoadFromDistributedCacheAsync(IEnumerable<string> cacheKeys, CancellationToken cancellationToken = default)
    {
        var keys = cacheKeys as IReadOnlyCollection<string> ?? cacheKeys.ToArray();
        if (keys.Count == 0) return;

        // Single-key fast path (e.g. WarmComponentAsync after a publish broadcast):
        // skip Parallel.ForEachAsync overhead and use the per-key SnapshotUpsert directly.
        if (keys.Count == 1)
        {
            await LoadSingleKeyAsync(keys.First(), cancellationToken).ConfigureAwait(false);
            return;
        }

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationWarmup, componentType: ComponentKeyName);
        CacheActivityHelper.SetItemCount(activity, keys.Count);

        var collected = new ConcurrentBag<(string CacheKey, T Entity)>();

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Max(1, _warmupOptions.MaxConcurrencyPerCacheSet)
        };

        await Parallel.ForEachAsync(keys, options, async (cacheKey, ct) =>
        {
            try
            {
                var entity = await distributedCache.GetAsync<T>(cacheKey, ct).ConfigureAwait(false);
                if (entity is not null)
                {
                    EnsureReferenceIsSet(entity, cacheKey);
                    collected.Add((cacheKey, entity));
                    return;
                }

                var parsed = TryParseCacheKey(cacheKey);
                if (parsed is null) return;

                var result = await backend.LoadAsync(parsed.Value.Domain, parsed.Value.Key, parsed.Value.Version, ct)
                    .ConfigureAwait(false);
                if (result is { IsSuccess: true, Value: not null })
                    collected.Add((CreateCacheKey(result.Value), result.Value));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to warm in-memory cache for key {CacheKey}", cacheKey);
            }
        }).ConfigureAwait(false);

        if (!collected.IsEmpty)
            SnapshotUpsertBatch(collected.ToArray());
    }

    private async Task LoadSingleKeyAsync(string cacheKey, CancellationToken cancellationToken)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationWarmup, cacheKey, ComponentKeyName);

        try
        {
            var entity = await distributedCache.GetAsync<T>(cacheKey, cancellationToken).ConfigureAwait(false);
            if (entity is not null)
            {
                CacheActivityHelper.SetCacheHit(activity, true);
                EnsureReferenceIsSet(entity, cacheKey);
                SnapshotUpsert(cacheKey, entity);
                return;
            }

            CacheActivityHelper.SetCacheHit(activity, false);

            var parsed = TryParseCacheKey(cacheKey);
            if (parsed is null) return;

            var result = await backend.LoadAsync(parsed.Value.Domain, parsed.Value.Key, parsed.Value.Version, cancellationToken)
                .ConfigureAwait(false);
            if (result is { IsSuccess: true, Value: not null })
                SnapshotUpsert(CreateCacheKey(result.Value), result.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            CacheActivityHelper.SetError(activity, ex);
            _logger.LogWarning(ex, "Failed to warm in-memory cache for key {CacheKey}", cacheKey);
        }
    }

    public void Dispose()
    {
        // Snapshot only contains managed objects, no cleanup needed
    }

    // ----------------
    // Snapshot helpers (CAS-based updates)
    // ----------------

    private void SnapshotUpsert(string cacheKey, T entity)
    {
        while (true)
        {
            var current = _snapshot;

            var entries = current.Entries.ToDictionary(k => k.Key, v => v.Value);
            var index = current.Index.ToDictionary(
                k => k.Key,
                v => new SortedSet<string>(v.Value));

            entries[cacheKey] = new CacheItem<T>(entity);
            UpdateIndex(index, entity);

            var newSnapshot = new CacheSnapshot<T>(
                entries.ToImmutableDictionary(),
                index.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

            var original = Interlocked.CompareExchange(ref _snapshot, newSnapshot, current);
            if (ReferenceEquals(original, current))
                break; // CAS succeeded
        }
    }

    /// <summary>
    /// Bulk upsert that rebuilds the snapshot once per CAS attempt (vs. once per item).
    /// Used by warm-up paths so live readers observe a single atomic swap regardless of N.
    /// Concurrent writers (e.g. publish path) on the same snapshot will trigger a CAS retry.
    /// </summary>
    private void SnapshotUpsertBatch(IReadOnlyCollection<(string CacheKey, T Entity)> items)
    {
        if (items.Count == 0) return;

        while (true)
        {
            var current = _snapshot;

            var entries = current.Entries.ToDictionary(k => k.Key, v => v.Value);
            var index = current.Index.ToDictionary(
                k => k.Key,
                v => new SortedSet<string>(v.Value));

            foreach (var (cacheKey, entity) in items)
            {
                entries[cacheKey] = new CacheItem<T>(entity);
                UpdateIndex(index, entity);
            }

            var newSnapshot = new CacheSnapshot<T>(
                entries.ToImmutableDictionary(),
                index.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

            var original = Interlocked.CompareExchange(ref _snapshot, newSnapshot, current);
            if (ReferenceEquals(original, current))
                break;
        }
    }

    private void SnapshotRemove(string cacheKey)
    {
        while (true)
        {
            var current = _snapshot;

            if (!current.Entries.ContainsKey(cacheKey))
                return;

            var entries = current.Entries.ToDictionary(k => k.Key, v => v.Value);
            var index = current.Index.ToDictionary(
                k => k.Key,
                v => new SortedSet<string>(v.Value));

            if (!entries.Remove(cacheKey, out _))
                return;

            // Rebuild index
            index.Clear();
            foreach (var kvp in entries)
            {
                UpdateIndex(index, kvp.Value.Value);
            }

            var newSnapshot = new CacheSnapshot<T>(
                entries.ToImmutableDictionary(),
                index.ToImmutableDictionary(kvp => kvp.Key, kvp => new SortedSet<string>(kvp.Value)));

            var original = Interlocked.CompareExchange(ref _snapshot, newSnapshot, current);
            if (ReferenceEquals(original, current))
                break; // CAS succeeded
        }
    }

    private Task UpsertLocalAsync(T entity, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var cacheKey = CreateCacheKey(entity);
            SnapshotUpsert(cacheKey, entity);
        }, cancellationToken);
    }

    // ----------------
    // Key / index / reference helpers
    // ----------------

    /// <summary>
    /// Returns cached vidx results when fresh enough, otherwise fetches from Redis
    /// and stores the result for <see cref="VidxCacheTtl"/>.
    /// Null/empty results are also cached to avoid redundant Redis roundtrips when the
    /// index has not been populated yet (e.g. after startup). The local in-memory snapshot
    /// still provides the correct "latest" via <c>ChooseHigher</c>, and when both sources
    /// are empty the caller falls back to the DB which then populates the index via
    /// <see cref="SetAsync"/> (which also invalidates this cache).
    /// </summary>
    private async Task<SortedSet<string>?> GetCachedVersionsAsync(
        string domain, string key, CancellationToken cancellationToken)
    {
        var vidxKey = CreateIndexKey(domain, key);

        if (_vidxCache.TryGetValue(vidxKey, out var cached) &&
            (DateTime.UtcNow - cached.FetchedAt) < VidxCacheTtl)
        {
            return cached.Versions;
        }

        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationVersionIndex, vidxKey, ComponentKeyName);

        var versions = await versionIndex.GetVersionsAsync(
            ComponentKeyName, domain, key, cancellationToken);

        CacheActivityHelper.SetCacheHit(activity, versions is { Count: > 0 });

        _vidxCache[vidxKey] = (versions, DateTime.UtcNow);
        return versions;
    }

    private static string CreateCacheKey(T entity)
        => CreateCacheKey(entity.Domain, entity.Key, entity.Version);

    private static string CreateCacheKey(string domain, string key, string? version)
        => $"{ComponentKeyName}:{domain}:{key}:{version}";

    /// <summary>
    /// Picks the SemVer-higher of two candidate versions. Either may be null/empty.
    /// Used to reconcile a local-snapshot resolution with the cross-pod Redis vidx
    /// resolution so the freshest known version always wins.
    /// </summary>
    private static string? ChooseHigher(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a)) return b;
        if (string.IsNullOrEmpty(b)) return a;
        return new SemVersionComparer().Compare(a, b) >= 0 ? a : b;
    }

    private static string CreateIndexKey(string domain, string name)
        => $"{domain}:{name}";

    private void UpdateIndex(Dictionary<string, SortedSet<string>> index, T entity)
    {
        var indexKey = CreateIndexKey(entity.Domain, entity.Key);
        if (!index.TryGetValue(indexKey, out var set))
        {
            set = new SortedSet<string>(new SemVersionComparer());
            index[indexKey] = set;
        }

        if (!string.IsNullOrEmpty(entity.Version))
            set.Add(entity.Version);
    }

    private (string Domain, string Key, string? Version)? TryParseCacheKey(string cacheKey)
    {
        var match = Regex.Match(cacheKey, @"^([^:]+):([^:]+):([^:]+):([^:]+)$");
        if (!match.Success)
            return null;

        var domain = match.Groups[2].Value;
        var key = match.Groups[3].Value;
        var version = match.Groups[4].Value;

        return (domain, key, version);
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
        if (parsed is null)
            return;

        var (domain, key, version) = parsed.Value;

        try
        {
            var reference = new Reference(key, domain, entity.ComponentKey, version!);
            entity.SetReference(reference);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting reference from cache key: {CacheKey}", cacheKey);
        }
    }
}