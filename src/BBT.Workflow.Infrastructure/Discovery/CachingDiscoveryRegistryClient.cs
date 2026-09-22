using System.Collections.Concurrent;
using System.Diagnostics;
using BBT.Aether.DistributedCache;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Writes registrations into the discovery cache. Implemented by
/// <see cref="CachingDiscoveryRegistryClient"/> so the bulk refresher fills exactly the keys the
/// read path looks in.
/// </summary>
/// <remarks>
/// A separate interface rather than a second key-building implementation in the refresher: two
/// pieces of code agreeing on a key format by convention is how a cache ends up writing entries
/// nothing ever reads, which looks identical to a healthy cache from the outside.
/// </remarks>
public interface IDiscoveryCacheWriter
{
    /// <summary>Publishes a full set of registrations.</summary>
    Task SetAsync(IReadOnlyList<DomainRegistration> registrations, CancellationToken cancellationToken);

    /// <summary>Reads the shared refresh marker, or <c>null</c> when nothing has claimed it.</summary>
    Task<string?> GetRefreshMarkerAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Claims the refresh. Written with <c>RefreshIntervalSeconds</c> as its lifetime, which means
    /// two different things: in periodic mode it expires with the window it defines; with that value
    /// at <c>0</c> — the default — it is written without an expiry and records that the cluster's
    /// cache has been filled, which is what lets each pod's warm-up loop stop after one success.
    /// A forced refresh does not consult it.
    /// </summary>
    Task SetRefreshMarkerAsync(CancellationToken cancellationToken);

}

/// <summary>
/// Read-through cache in front of <see cref="DiscoveryRegistryClient"/>: in-process L1, shared
/// distributed L2, live registry on a miss.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered only under <c>ServiceDiscovery:Provider=http</c>, and only when
/// <c>ServiceDiscovery:Cache:Enabled</c> is true.</b> Both conditions are decided where the client is
/// registered, not by a branch in here. That matters twice over: the Dapr provider's registry reads
/// stay genuinely untouched rather than merely bypassed, and turning the cache off leaves no code
/// path that could still read an entry written before the flip.
/// </para>
/// <para>
/// <b>What is cached is a <see cref="DomainRegistration"/>, never a <c>DiscoveryEndpoint</c>.</b> An
/// endpoint's <c>Kind</c> depends on the calling site's <c>preferredKind</c>, so caching one under a
/// domain-keyed entry lets a Dapr-preferring trigger task and a URL call site overwrite each other's
/// answer. The registration is caller-independent, which is what makes a single key per domain
/// correct. This was a real defect in the implementation removed in <c>79da3b6f</c>.
/// </para>
/// <para>
/// <b>A hit makes no network call.</b> That is the entire difference from the removed cache, which
/// revalidated over HTTP on every hit against an endpoint with no conditional-request support — so it
/// paid full registry latency AND carried a staleness window. Staleness here is bounded instead by
/// the recorded fetch age and by the periodic bulk refresh.
/// </para>
/// </remarks>
public sealed class CachingDiscoveryRegistryClient(
    DiscoveryRegistryClient inner,
    IDistributedCacheService distributedCache,
    IDiscoveryL1Cache l1Cache,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    TimeProvider timeProvider,
    ILogger<CachingDiscoveryRegistryClient> logger) : IDiscoveryRegistryClient, IDiscoveryCacheWriter, IDiscoveryEndpointFeedback
{
    /// <summary>
    /// Key prefix. The <c>v1</c> segment is a shape generation: bump it whenever
    /// <see cref="CachedDomainRegistration"/> changes, so an old entry becomes a miss instead of a
    /// deserialization failure on a hot path.
    /// </summary>
    private const string KeyPrefix = "discovery:domain:v1:";

    /// <summary>
    /// Shared marker naming the refresh window that has already been served.
    /// </summary>
    internal const string RefreshMarkerKey = "discovery:bulk:v1:refreshed-at";

    private const string OperationGet = "Cache.Get";
    private const string OperationSet = "Cache.Set";
    private const string OperationRemove = "Cache.Remove";

    /// <summary>
    /// Registry reads in flight, so concurrent misses for one domain share a single lookup instead
    /// of each paying full registry latency. A pod starts cold, and the first burst of cross-domain
    /// traffic after a rollout misses on every call at once.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Result<DomainRegistration>>>> _inFlight = new();

    /// <summary>
    /// When each domain was last evicted because something could not reach it. Bounded by the domain
    /// count, like the cache itself.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUnreachableEviction = new();

    private DiscoveryCacheOptions Cache => serviceDiscoveryOptions.Value.Cache;

    /// <inheritdoc />
    public async Task<Result<DomainRegistration>> LookupAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        // Gated here as well as at registration: flipping discovery off must not leave a populated
        // cache answering on its behalf.
        if (!serviceDiscoveryOptions.Value.Enabled)
            return await inner.LookupAsync(domain, cancellationToken);

        var cacheKey = BuildKey(domain);

        if (TryReadFresh(l1Cache.TryGet(cacheKey)) is { } fromL1)
        {
            MarkResolutionCached(fromL1.FetchedAtUtc);
            return Result<DomainRegistration>.Ok(fromL1.Registration);
        }

        var fromL2 = TryReadFresh(await TryGetAsync(cacheKey, cancellationToken));
        if (fromL2 is not null)
        {
            l1Cache.Set(cacheKey, fromL2);
            MarkResolutionCached(fromL2.FetchedAtUtc);
            return Result<DomainRegistration>.Ok(fromL2.Registration);
        }

        logger.DomainNotFoundInCache(domain);

        return await ResolveCoalescedAsync(domain, cacheKey, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>The bulk read is the cache fill; serving it from the cache would be circular.</remarks>
    public Task<Result<IReadOnlyList<DomainRegistration>>> ListAllAsync(CancellationToken cancellationToken)
        => inner.ListAllAsync(cancellationToken);

    // ────────────────────────────────────────────────────────────────────
    // IDiscoveryCacheWriter
    // ────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SetAsync(
        IReadOnlyList<DomainRegistration> registrations,
        CancellationToken cancellationToken)
    {
        var fetchedAt = timeProvider.GetUtcNow();

        foreach (var registration in registrations)
        {
            var cacheKey = BuildKey(registration.DomainName);
            var entry = new CachedDomainRegistration(registration, fetchedAt);

            await TryWriteAsync(cacheKey, entry, Cache.L2TtlSeconds, cancellationToken);
            l1Cache.Set(cacheKey, entry);
        }
    }

    /// <inheritdoc />
    public Task<string?> GetRefreshMarkerAsync(CancellationToken cancellationToken)
        => TryGetRawAsync(RefreshMarkerKey, cancellationToken);

    /// <inheritdoc />
    public Task SetRefreshMarkerAsync(CancellationToken cancellationToken)
        => TryWriteAsync(
            RefreshMarkerKey,
            new RefreshMarker(timeProvider.GetUtcNow()),
            Cache.RefreshIntervalSeconds,
            cancellationToken);

    // ────────────────────────────────────────────────────────────────────
    // IDiscoveryEndpointFeedback
    // ────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Evicting is not the same as concluding the entry was wrong.</b> A correctly cached endpoint
    /// for a domain that is merely DOWN is evicted too, and the next resolution reads the registry and
    /// caches the same address again. That is the intended outcome: the cost is one registry read per
    /// cooldown window, and the alternative — trying to tell "moved" from "down" from a socket error —
    /// is not decidable here.
    /// </para>
    /// <para>
    /// The cooldown is the load guard that makes the above affordable. Without it a peer domain's
    /// outage becomes ours: every failed call evicts, and every eviction sends the next caller to the
    /// registry.
    /// </para>
    /// <para>
    /// Only the shared layer and THIS pod's in-process layer are cleared. Other pods keep their own
    /// copy for up to <c>Cache:L1TtlSeconds</c>, so recovery is per-pod-immediate and fleet-wide
    /// within that window — the same propagation rule every other invalidation here follows.
    /// </para>
    /// </remarks>
    public async Task ReportUnreachableAsync(
        string? domain,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(domain))
            return;

        var cooldownSeconds = Cache.UnreachableEvictionCooldownSeconds;

        // 0 switches failure-driven eviction off entirely; the entry then lives until an event
        // replaces it.
        if (cooldownSeconds <= 0)
            return;

        var cacheKey = BuildKey(domain);

        if (!TryClaimEviction(cacheKey, TimeSpan.FromSeconds(cooldownSeconds)))
        {
            logger.DiscoveryEndpointEvictionThrottled(domain, cooldownSeconds);
            return;
        }

        l1Cache.Remove(cacheKey);

        try
        {
            await distributedCache.RemoveAsync(cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            // Swallowed like every other cache operation: this method is called from a catch block
            // that is about to rethrow the caller's real failure, and must not replace it.
            logger.DiscoveryCacheOperationFailed(ex, OperationRemove, cacheKey);
        }

        logger.DiscoveryEndpointEvicted(domain, reason);
    }

    // ────────────────────────────────────────────────────────────────────
    // Private
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Claims the right to evict <paramref name="cacheKey"/> now, or refuses because the previous
    /// eviction is still inside the cooldown.
    /// </summary>
    /// <remarks>
    /// The loop is the check-and-set: concurrent failures for one domain — the normal shape, since a
    /// moved domain fails every in-flight call at once — must produce exactly one eviction, and a
    /// plain read-then-write would let all of them through.
    /// </remarks>
    private bool TryClaimEviction(string cacheKey, TimeSpan cooldown)
    {
        var now = timeProvider.GetUtcNow();

        while (true)
        {
            if (_lastUnreachableEviction.TryGetValue(cacheKey, out var last))
            {
                if (now - last < cooldown)
                    return false;

                if (_lastUnreachableEviction.TryUpdate(cacheKey, now, last))
                    return true;

                // Another thread moved the stamp between the read and the update; re-read it.
                continue;
            }

            if (_lastUnreachableEviction.TryAdd(cacheKey, now))
                return true;
        }
    }

    /// <summary>
    /// Lowercased so the write and read paths cannot disagree.
    /// </summary>
    /// <remarks>
    /// The registry echoes whatever spelling a domain registered with, while callers use whatever
    /// spelling their component authored. If those two ever produce different keys, every lookup for
    /// that domain misses and goes live — forever — while the cache still reports entries and no
    /// errors. A cache that is silently useless is worse than no cache, because nobody goes looking.
    /// </remarks>
    private static string BuildKey(string domain) => KeyPrefix + domain.ToLowerInvariant();

    /// <summary>
    /// Returns the entry unless an age ceiling is configured and the entry is past it.
    /// </summary>
    /// <remarks>
    /// <c>L2TtlSeconds = 0</c> — the default — means no ceiling: invalidation is an event
    /// (<c>definitions/publish/completed</c>, a forced refresh, a transport failure), not a clock.
    /// The negative-age guard survives that change because it is about a different failure: a clock
    /// that moved backwards produces an entry from the future, and reading it as "infinitely fresh"
    /// would be wrong under either policy.
    /// </remarks>
    private CachedDomainRegistration? TryReadFresh(CachedDomainRegistration? entry)
    {
        if (entry is null)
            return null;

        var age = timeProvider.GetUtcNow() - entry.FetchedAtUtc;

        if (age < TimeSpan.Zero)
            return null;

        var ttlSeconds = Cache.L2TtlSeconds;

        return ttlSeconds <= 0 || age <= TimeSpan.FromSeconds(ttlSeconds)
            ? entry
            : null;
    }

    /// <summary>
    /// Shares one registry lookup between every caller currently missing the same domain.
    /// </summary>
    private async Task<Result<DomainRegistration>> ResolveCoalescedAsync(
        string domain,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var resolution = _inFlight.GetOrAdd(
            cacheKey,
            _ => new Lazy<Task<Result<DomainRegistration>>>(
                () => ResolveAndCacheAsync(domain, cacheKey, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await resolution.Value;
        }
        finally
        {
            _inFlight.TryRemove(cacheKey, out _);
        }
    }

    /// <summary>
    /// Reads the registry and caches a successful answer.
    /// </summary>
    /// <remarks>
    /// Failures are deliberately never cached — not even as a negative entry. A domain that
    /// registers a moment from now must start working on its next call rather than after a TTL. The
    /// cost of that choice is a repeated live lookup for a genuinely absent domain, which is exactly
    /// what happens today.
    /// </remarks>
    private async Task<Result<DomainRegistration>> ResolveAndCacheAsync(
        string domain,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var result = await inner.LookupAsync(domain, cancellationToken);

        if (!result.IsSuccess)
            return result;

        var entry = new CachedDomainRegistration(result.Value!, timeProvider.GetUtcNow());

        await TryWriteAsync(cacheKey, entry, Cache.L2TtlSeconds, cancellationToken);
        l1Cache.Set(cacheKey, entry);

        return result;
    }

    /// <summary>
    /// Tags the active <c>Discovery.Resolve</c> span as cache-served, with the entry's age.
    /// </summary>
    /// <remarks>
    /// The age is the tag that matters. Without it there is no way to answer "was this routed by a
    /// stale entry?" during an incident — which is precisely the question that got the previous
    /// discovery cache deleted rather than debugged.
    /// </remarks>
    private void MarkResolutionCached(DateTimeOffset fetchedAtUtc)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        activity.SetTag(
            TelemetryConstants.TagNames.DiscoveryResolution,
            TelemetryConstants.DiscoveryResolutions.Cache);

        activity.SetTag(
            TelemetryConstants.TagNames.DiscoveryCacheAgeSeconds,
            (int)(timeProvider.GetUtcNow() - fetchedAtUtc).TotalSeconds);
    }

    private async Task<CachedDomainRegistration?> TryGetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            return await distributedCache.GetAsync<CachedDomainRegistration>(cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            // Includes a deserialization failure from an entry written by an older shape. Dropping it
            // and answering "miss" keeps the read path correct; the next successful lookup rewrites it.
            logger.DiscoveryCacheOperationFailed(ex, OperationGet, cacheKey);
            l1Cache.Remove(cacheKey);
            return null;
        }
    }

    private async Task<string?> TryGetRawAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            var marker = await distributedCache.GetAsync<RefreshMarker>(cacheKey, cancellationToken);
            return marker?.RefreshedAtUtc.ToString("O");
        }
        catch (Exception ex)
        {
            logger.DiscoveryCacheOperationFailed(ex, OperationGet, cacheKey);
            return null;
        }
    }

    private async Task TryWriteAsync<T>(
        string cacheKey,
        T value,
        int ttlSeconds,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            // ttl <= 0 writes no expiry at all: the entry is invalidated by an event, and a store
            // that quietly ignores TTL metadata (which the Dapr layer allows) then changes nothing.
            var entryOptions = new DistributedCacheEntryOptions();

            if (ttlSeconds > 0)
                entryOptions.AbsoluteExpiration = timeProvider.GetUtcNow().AddSeconds(ttlSeconds);

            await distributedCache.SetAsync(
                cacheKey,
                value,
                entryOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Swallowed: a cache that cannot be written still returns a correct answer, and failing
            // the caller for it would turn a degraded cache into a degraded runtime.
            logger.DiscoveryCacheOperationFailed(ex, OperationSet, cacheKey);
        }
    }

    /// <summary>
    /// The shared refresh-window marker's payload.
    /// </summary>
    private sealed record RefreshMarker(DateTimeOffset RefreshedAtUtc);
}
