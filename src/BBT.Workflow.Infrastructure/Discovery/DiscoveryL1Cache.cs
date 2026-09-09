using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// A registration as stored in the cache, carrying the instant it was read from the registry.
/// </summary>
/// <param name="Registration">The registration itself.</param>
/// <param name="FetchedAtUtc">When the registry answered.</param>
/// <remarks>
/// The timestamp travels WITH the value rather than being left to the cache store's own expiry, and
/// that is load-bearing rather than defensive. The distributed layer is Dapr-backed, so an absolute
/// expiration becomes the state store's <c>ttlInSeconds</c> metadata — which a component that does
/// not implement TTL ignores <i>silently</i>. Entries would then never expire, producing exactly the
/// unbounded staleness that got the previous discovery cache deleted, with nothing in code review or
/// the logs to reveal it. Validating the age on every read makes correctness independent of the
/// component; the store's TTL degrades to garbage collection.
/// </remarks>
public sealed record CachedDomainRegistration(DomainRegistration Registration, DateTimeOffset FetchedAtUtc);

/// <summary>
/// In-process (L1) layer in front of the distributed discovery cache.
/// </summary>
public interface IDiscoveryL1Cache
{
    /// <summary>Reads an entry, or <c>null</c> when absent or disabled.</summary>
    CachedDomainRegistration? TryGet(string cacheKey);

    /// <summary>Stores an entry for <c>L1TtlSeconds</c>.</summary>
    void Set(string cacheKey, CachedDomainRegistration entry);

    /// <summary>Drops an entry.</summary>
    void Remove(string cacheKey);
}

/// <inheritdoc />
/// <remarks>
/// Backed by a <b>private</b> <see cref="MemoryCache"/> rather than the DI <c>IMemoryCache</c>, so
/// the "distributed cache for business data" rule stays intact everywhere else — the same reasoning
/// as <c>ComponentL1Cache</c>. Registrations are a handful of short strings each, so entries are
/// bounded by count rather than by byte size and are stored as objects rather than serialized.
/// </remarks>
public sealed class DiscoveryL1Cache : IDiscoveryL1Cache, IDisposable
{
    /// <summary>
    /// Entry ceiling. Far above any plausible domain count, so it is a runaway guard rather than an
    /// eviction policy — an L1 that evicted under normal load would quietly stop helping.
    /// </summary>
    private const int EntryLimit = 2048;

    private readonly MemoryCache? _cache;
    private readonly TimeSpan _timeToLive;

    /// <summary>
    /// Creates the layer, or a disabled no-op when <c>Cache:L1Enabled</c> is false.
    /// </summary>
    public DiscoveryL1Cache(IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions)
    {
        var cache = serviceDiscoveryOptions.Value.Cache;
        _timeToLive = TimeSpan.FromSeconds(cache.L1TtlSeconds);

        if (cache.L1Enabled)
            _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = EntryLimit });
    }

    /// <inheritdoc />
    public CachedDomainRegistration? TryGet(string cacheKey)
        => _cache is not null && _cache.TryGetValue(cacheKey, out CachedDomainRegistration? entry)
            ? entry
            : null;

    /// <inheritdoc />
    public void Set(string cacheKey, CachedDomainRegistration entry)
        => _cache?.Set(cacheKey, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _timeToLive,
            Size = 1
        });

    /// <inheritdoc />
    public void Remove(string cacheKey) => _cache?.Remove(cacheKey);

    /// <inheritdoc />
    public void Dispose() => _cache?.Dispose();
}
