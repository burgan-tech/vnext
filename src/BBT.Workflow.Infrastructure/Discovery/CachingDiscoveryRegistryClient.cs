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

    /// <summary>Reads the shared refresh marker, or <c>null</c> when this window is unclaimed.</summary>
    Task<string?> GetRefreshMarkerAsync(CancellationToken cancellationToken);

    /// <summary>Claims the current refresh window.</summary>
    Task SetRefreshMarkerAsync(CancellationToken cancellationToken);

    /// <summary>Drops the refresh marker so the next tick refreshes immediately.</summary>
    Task ClearRefreshMarkerAsync(CancellationToken cancellationToken);
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
    ILogger<CachingDiscoveryRegistryClient> logger) : IDiscoveryRegistryClient, IDiscoveryCacheWriter
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

    /// <inheritdoc />
    public async Task ClearRefreshMarkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await distributedCache.RemoveAsync(RefreshMarkerKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.DiscoveryCacheOperationFailed(ex, OperationRemove, RefreshMarkerKey);
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // Private
    // ────────────────────────────────────────────────────────────────────

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
    /// Returns the entry only while it is inside the configured age; otherwise <c>null</c>.
    /// </summary>
    private CachedDomainRegistration? TryReadFresh(CachedDomainRegistration? entry)
    {
        if (entry is null)
            return null;

        var age = timeProvider.GetUtcNow() - entry.FetchedAtUtc;

        // Negative age means a clock moved backwards; treat it as unusable rather than as infinitely
        // fresh.
        return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(Cache.L2TtlSeconds)
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
            await distributedCache.SetAsync(
                cacheKey,
                value,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = timeProvider.GetUtcNow().AddSeconds(ttlSeconds)
                },
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
