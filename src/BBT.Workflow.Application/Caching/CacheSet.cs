using System.Collections.Immutable;
using System.Text.RegularExpressions;
using BBT.Aether.DistributedCache;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides a thread-safe cache implementation using an immutable snapshot model.
/// Supports local in-memory caching, distributed caching, and database fallback.
/// </summary>
/// <typeparam name="T">The type of entity to cache</typeparam>
public class CacheSet<T>(
    IDistributedCacheService distributedCache,
    ICacheBackend<T> backend,
    ILogger<CacheSet<T>> logger)
    : ICacheSet<T>
    where T : class, IDomainEntity, IReferenceSetter
{
    private readonly ILogger _logger = logger;

    // Immutable snapshot holding all local cache state
    private CacheSnapshot<T> _snapshot = new(
        ImmutableDictionary<string, CacheItem<T>>.Empty,
        ImmutableDictionary<string, SortedSet<string>>.Empty);

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
        try
        {
            var fromDistributed = await distributedCache.GetAsync<T>(cacheKey, cancellationToken);
            if (fromDistributed is not null)
            {
                EnsureReferenceIsSet(fromDistributed, cacheKey);
                // Asynchronously update local cache
                _ = UpsertLocalAsync(fromDistributed, cancellationToken);
                return Result<T>.Ok(fromDistributed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from distributed cache for {CacheKey}", cacheKey);
            // Continue to backend fallback
        }

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
        _ = SetAsync(fromDb, cancellationToken); // Async write to both local and distributed

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
        try
        {
            await distributedCache.SetAsync(cacheKey, entity, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error writing to distributed cache for {CacheKey}", cacheKey);
            // Local cache is already updated, distributed cache failure is logged but not blocking
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

        string? latestVersion = null;

        if (snap.Index.TryGetValue(indexKey, out var versions) && versions.Count > 0)
        {
            latestVersion = versions
                .OrderByDescending(v => v, new SemVersionComparer())
                .FirstOrDefault();
        }

        // If we have a latest version from the index, find it in the snapshot
        if (!string.IsNullOrEmpty(latestVersion))
        {
            foreach (var entry in snap.Entries.Values)
            {
                var entity = entry.Value;
                if (string.Equals(entity.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entity.Key, name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(entity.Version, latestVersion, StringComparison.OrdinalIgnoreCase))
                {
                    entry.UpdateAccess();
                    return Result<T>.Ok(entity);
                }
            }
        }

        // Not in snapshot: load from backend
        var fromDbResult = await backend.LoadAsync(domain, name, version: null, cancellationToken);
        
        if (!fromDbResult.IsSuccess)
        {
            return fromDbResult;
        }

        var fromDb = fromDbResult.Value!;
        var cacheKey = CreateCacheKey(fromDb);
        EnsureReferenceIsSet(fromDb, cacheKey);
        _ = SetAsync(fromDb, cancellationToken);

        return Result<T>.Ok(fromDb);
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
            var cacheKey = $"{typeof(T).Name}:{domain}:{key}:{version}";
            return await GetAsync(cacheKey, cancellationToken);
        }

        // 3) For artifact or partial version → use smart matching
        var snap = _snapshot;
        var indexKey = CreateIndexKey(domain, key);

        // Get all versions from index
        if (snap.Index.TryGetValue(indexKey, out var versions) && versions.Count > 0)
        {
            // Use InstanceDataVersionComparer.FindBestMatch to find the best matching version
            var bestMatch = InstanceDataVersionComparer.FindBestMatch(versions, version);

            if (!string.IsNullOrEmpty(bestMatch))
            {
                // Find the entity with the best matching version in snapshot
                foreach (var entry in snap.Entries.Values)
                {
                    var entity = entry.Value;
                    if (string.Equals(entity.Domain, domain, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entity.Key, key, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entity.Version, bestMatch, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.UpdateAccess();
                        return Result<T>.Ok(entity);
                    }
                }
            }
        }

        // 4) Not in snapshot: try distributed cache with pattern matching or load from backend
        // For artifact/partial versions, we need to load from backend with smart matching
        var fromDbResult = await backend.LoadAsync(domain, key, version, cancellationToken);

        if (!fromDbResult.IsSuccess)
        {
            return fromDbResult;
        }

        var fromDb = fromDbResult.Value!;
        var foundCacheKey = CreateCacheKey(fromDb);
        EnsureReferenceIsSet(fromDb, foundCacheKey);
        _ = SetAsync(fromDb, cancellationToken);

        return Result<T>.Ok(fromDb);
    }

    public async Task<Result> InvalidateAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        // Remove from snapshot
        SnapshotRemove(cacheKey);

        // Remove from distributed cache - network errors will throw (expected per Railway Pattern)
        try
        {
            await distributedCache.RemoveAsync(cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing from distributed cache for {CacheKey}", cacheKey);
            // Local cache is already removed, distributed cache failure is logged but not blocking
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

    private static string CreateCacheKey(T entity)
        => $"{(typeof(T) as IDomainEntity)?.ComponentKey}:{entity.Domain}:{entity.Key}:{entity.Version}";

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