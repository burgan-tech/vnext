using BBT.Aether.DistributedCache;
using BBT.Workflow.Caching;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.Caching;

/// <summary>
/// Redis-backed implementation of <see cref="IComponentVersionIndex"/>.
/// Stores the list of known versions per component under "vidx:{componentKey}:{domain}:{key}".
/// All failures are logged and swallowed so the system can self-heal via DB fallback.
/// </summary>
public sealed class RedisComponentVersionIndex(
    IDistributedCacheService cache,
    ILogger<RedisComponentVersionIndex> logger) : IComponentVersionIndex
{
    // Index TTL is intentionally longer than the body TTL (CacheSet uses 12h for items).
    // Stale index entries are self-healing: a body cache miss will trigger a single-version
    // DB load that re-populates the index.
    private static readonly TimeSpan IndexTtl = TimeSpan.FromHours(24);

    private static string BuildIndexKey(string componentKey, string domain, string key)
        => $"vidx:{componentKey}:{domain}:{key}";

    public async Task<SortedSet<string>?> GetVersionsAsync(
        string componentKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default)
    {
        var indexKey = BuildIndexKey(componentKey, domain, key);

        try
        {
            var raw = await cache.GetAsync<string[]>(indexKey, cancellationToken);
            if (raw is null || raw.Length == 0)
                return null;

            return new SortedSet<string>(raw, new SemVersionComparer());
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Version index read failed for {IndexKey}, falling through to DB",
                indexKey);
            return null;
        }
    }

    public async Task AddVersionAsync(
        string componentKey,
        string domain,
        string key,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        var indexKey = BuildIndexKey(componentKey, domain, key);

        try
        {
            // Read-modify-write. A distributed lock would be safer, but the race window is
            // tolerable here: the worst case is that a version is briefly missing from the
            // index and a subsequent get falls back to the DB to repopulate it.
            var existing = await cache.GetAsync<string[]>(indexKey, cancellationToken)
                           ?? Array.Empty<string>();

            if (existing.Contains(version, StringComparer.Ordinal))
                return;

            var updated = existing.Append(version).Distinct(StringComparer.Ordinal).ToArray();

            await cache.SetAsync(
                indexKey,
                updated,
                new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.Add(IndexTtl) },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Version index add failed for {IndexKey} version {Version}",
                indexKey,
                version);
        }
    }

    public async Task RemoveVersionAsync(
        string componentKey,
        string domain,
        string key,
        string version,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        var indexKey = BuildIndexKey(componentKey, domain, key);

        try
        {
            var existing = await cache.GetAsync<string[]>(indexKey, cancellationToken);
            if (existing is null || existing.Length == 0)
                return;

            var updated = existing
                .Where(v => !string.Equals(v, version, StringComparison.Ordinal))
                .ToArray();

            if (updated.Length == 0)
            {
                await cache.RemoveAsync(indexKey, cancellationToken);
                return;
            }

            if (updated.Length == existing.Length)
                return;

            await cache.SetAsync(
                indexKey,
                updated,
                new DistributedCacheEntryOptions { AbsoluteExpiration = DateTimeOffset.UtcNow.Add(IndexTtl) },
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Version index remove failed for {IndexKey} version {Version}",
                indexKey,
                version);
        }
    }

    public async Task RemoveAllVersionsAsync(
        string componentKey,
        string domain,
        string key,
        CancellationToken cancellationToken = default)
    {
        var indexKey = BuildIndexKey(componentKey, domain, key);

        try
        {
            await cache.RemoveAsync(indexKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Version index remove-all failed for {IndexKey}", indexKey);
        }
    }
}
