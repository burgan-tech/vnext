using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DistributedCache;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins <see cref="CachingDiscoveryRegistryClient"/> — the read-through cache in front of the
/// discovery registry, active only under <c>ServiceDiscovery:Provider=http</c>.
/// </summary>
/// <remarks>
/// <para>
/// A cache of broadly this shape was shipped once and deleted (<c>79da3b6f</c>) because it carried a
/// staleness window without actually saving anything: it revalidated over HTTP on every hit against
/// an endpoint with no conditional-request support. Several tests here exist specifically to fail if
/// that bargain is ever re-struck — <see cref="Hit_serves_from_cache_without_any_http_call"/> above
/// all, which the removed implementation could not have passed.
/// </para>
/// <para>
/// The rest pin the properties that make a bounded staleness window defensible: entries expire by
/// their recorded age rather than by trusting the store's TTL, failures are never cached, casing
/// cannot split the key space, and a broken cache degrades to a live lookup instead of an error.
/// </para>
/// </remarks>
public sealed class CachingDiscoveryRegistryClientTests
{
    private const string Domain = "lending";
    private const string BaseUrl = "https://discovery.test";

    // ────────────────────────────────────────────────────────────────────
    // The saving
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Hit_serves_from_cache_without_any_http_call()
    {
        var sut = CreateSut(out var handler, out _, out _);

        var first = await sut.LookupAsync(Domain, CancellationToken.None);
        var callsAfterFirst = handler.Requests.Count;

        var second = await sut.LookupAsync(Domain, CancellationToken.None);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        second.Value!.BaseUrl.ShouldBe(first.Value!.BaseUrl);

        // The whole point. The removed implementation issued a revalidation GET here, which is why it
        // cost the full registry latency on a "hit" and was correctly deleted.
        handler.Requests.Count.ShouldBe(callsAfterFirst);
        callsAfterFirst.ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_misses_for_one_domain_share_a_single_registry_lookup()
    {
        var sut = CreateSut(out var handler, out _, out _, delayMilliseconds: 50);

        var lookups = Enumerable.Range(0, 8)
            .Select(_ => sut.LookupAsync(Domain, CancellationToken.None))
            .ToArray();

        await Task.WhenAll(lookups);

        lookups.ShouldAllBe(t => t.Result.IsSuccess);

        // Without single-flight a pod that starts cold pays full registry latency once per concurrent
        // caller, which is exactly the burst a rollout produces.
        handler.Requests.Count.ShouldBe(1);
    }

    // ────────────────────────────────────────────────────────────────────
    // The bound
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entry_older_than_the_configured_age_is_ignored_even_when_the_store_still_holds_it()
    {
        const int ttlSeconds = 120;

        // Configured explicitly rather than leaning on the default, so this stays a test of the
        // mechanism and not of whatever the current window happens to be.
        var sut = CreateSut(out var handler, out _, out var clock,
            configureCache: c => c.L2TtlSeconds = ttlSeconds);

        await sut.LookupAsync(Domain, CancellationToken.None);
        handler.Requests.Count.ShouldBe(1);

        // The fake store never expires anything — which is precisely the regime a Dapr state store
        // without TTL support puts us in, silently. The age stamped on the entry is what has to bound
        // staleness there, so this test asserts the cache does NOT rely on the store expiring it.
        clock.Advance(TimeSpan.FromSeconds(ttlSeconds + 1));

        var afterExpiry = await sut.LookupAsync(Domain, CancellationToken.None);

        afterExpiry.IsSuccess.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Moved_base_url_is_observed_once_the_entry_ages_out()
    {
        const int ttlSeconds = 120;

        var currentBaseUrl = "https://old.lending.test";
        var sut = CreateSut(out _, out _, out var clock,
            baseUrlProvider: () => currentBaseUrl,
            configureCache: c => c.L2TtlSeconds = ttlSeconds);

        var before = await sut.LookupAsync(Domain, CancellationToken.None);
        before.Value!.BaseUrl.ShouldBe("https://old.lending.test");

        currentBaseUrl = "https://new.lending.test";

        // Still inside the window: serving the old address here is the bounded, accepted cost.
        clock.Advance(TimeSpan.FromSeconds(ttlSeconds / 2));
        var duringWindow = await sut.LookupAsync(Domain, CancellationToken.None);
        duringWindow.Value!.BaseUrl.ShouldBe("https://old.lending.test");

        // Past it, the move must be visible without any refresher having run — the dead-man's switch.
        clock.Advance(TimeSpan.FromSeconds(ttlSeconds + 1));
        var afterWindow = await sut.LookupAsync(Domain, CancellationToken.None);
        afterWindow.Value!.BaseUrl.ShouldBe("https://new.lending.test");
    }

    [Fact]
    public async Task Entry_with_a_future_timestamp_is_treated_as_unusable()
    {
        var sut = CreateSut(out var handler, out var store, out var clock);

        await sut.LookupAsync(Domain, CancellationToken.None);

        // A clock that jumped backwards would otherwise make an entry look infinitely fresh.
        clock.Advance(TimeSpan.FromSeconds(-600));

        await sut.LookupAsync(Domain, CancellationToken.None);

        handler.Requests.Count.ShouldBe(2);
        store.Entries.ShouldNotBeEmpty();
    }

    // ────────────────────────────────────────────────────────────────────
    // Failure handling
    // ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Failures_are_never_cached(HttpStatusCode status)
    {
        var sut = CreateSut(out var handler, out var store, out _, respond: _ => new HttpResponseMessage(status));

        var first = await sut.LookupAsync(Domain, CancellationToken.None);
        var second = await sut.LookupAsync(Domain, CancellationToken.None);

        first.IsSuccess.ShouldBeFalse();
        second.IsSuccess.ShouldBeFalse();

        // A domain registering a moment from now has to work on its next call, not after a TTL.
        store.Entries.Keys.ShouldNotContain(k => k.StartsWith("discovery:domain:v1:", StringComparison.Ordinal));
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_read_failure_degrades_to_a_live_lookup()
    {
        var sut = CreateSut(out var handler, out var store, out _);
        store.FailReads = true;

        var result = await sut.LookupAsync(Domain, CancellationToken.None);

        // A cache that cannot be read is a miss. Surfacing the failure would turn a degraded cache
        // into a degraded runtime.
        result.IsSuccess.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Cache_write_failure_still_returns_a_correct_answer()
    {
        var sut = CreateSut(out _, out var store, out _);
        store.FailWrites = true;

        var result = await sut.LookupAsync(Domain, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.DomainName.ShouldBe(Domain);
    }

    // ────────────────────────────────────────────────────────────────────
    // Key discipline
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Domain_name_casing_does_not_split_the_key_space()
    {
        var sut = CreateSut(out var handler, out _, out _);

        await sut.LookupAsync("Lending", CancellationToken.None);
        await sut.LookupAsync("lending", CancellationToken.None);
        await sut.LookupAsync("LENDING", CancellationToken.None);

        // If the write and read paths ever disagree on casing, every lookup misses and goes live
        // forever, while the cache still reports entries and logs no errors — a cache that is
        // silently useless, which nobody goes looking for.
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Warmed_entries_are_readable_through_the_lookup_path()
    {
        var sut = CreateSut(out var handler, out _, out _);

        await ((IDiscoveryCacheWriter)sut).SetAsync(
            [new DomainRegistration("Lending", "https://warm.lending.test", "lending-app", null)],
            CancellationToken.None);

        var result = await sut.LookupAsync("lending", CancellationToken.None);

        // The refresher and the read path must agree on the key format, which is why the writer is an
        // interface on this same class rather than a second key builder somewhere else.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.BaseUrl.ShouldBe("https://warm.lending.test");
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Discovery_disabled_bypasses_the_cache_entirely()
    {
        var sut = CreateSut(out var handler, out _, out _, discoveryEnabled: false);

        await sut.LookupAsync(Domain, CancellationToken.None);
        await sut.LookupAsync(Domain, CancellationToken.None);

        // Turning discovery off must not leave a populated cache answering on its behalf.
        handler.Requests.Count.ShouldBe(2);
    }

    // ────────────────────────────────────────────────────────────────────
    // Observability
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cache_hit_reports_itself_on_the_resolution_span()
    {
        // Unique to this test: the listener is scoped to the ActivitySource, not to this resolver,
        // and xUnit runs test classes in parallel — a same-named span from a concurrent test would
        // otherwise land here too.
        const string domain = "cache-hit-span-probe";

        using var recorded = new RecordedActivities();

        var sut = CreateSut(out _, out _, out var clock, domain: domain);
        var provider = CreateProvider(sut);

        await provider.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(7));
        await provider.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);

        var spans = recorded.For(domain);
        spans.Count.ShouldBe(2);

        // The provider tags "registry" AFTER the registry client returns, so an unconditional set
        // there would overwrite the decorator's tag on every hit — leaving the span permanently
        // reporting "registry" and the cache invisible in traces, with no way to answer "was this
        // routed by a stale entry?" during an incident.
        spans[1].GetTagItem(TelemetryConstants.TagNames.DiscoveryResolution)
            .ShouldBe(TelemetryConstants.DiscoveryResolutions.Cache);

        spans[1].GetTagItem(TelemetryConstants.TagNames.DiscoveryCacheAgeSeconds).ShouldBe(7);
    }

    [Fact]
    public async Task A_miss_still_reports_registry_on_the_resolution_span()
    {
        const string domain = "cache-miss-span-probe";

        using var recorded = new RecordedActivities();

        var provider = CreateProvider(CreateSut(out _, out _, out _, domain: domain));

        await provider.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);

        recorded.For(domain).ShouldHaveSingleItem()
            .GetTagItem(TelemetryConstants.TagNames.DiscoveryResolution)
            .ShouldBe(TelemetryConstants.DiscoveryResolutions.Registry);
    }

    /// <summary>
    /// Collects <c>Discovery.Resolve/*</c> spans.
    /// </summary>
    /// <remarks>
    /// The source name is a hardcoded literal rather than
    /// <c>PipelineStepActivityHelper.ActivitySource.Name</c>, for the reason documented on
    /// <c>DomainDiscoveryResolverSpanTests</c>: the first access to that static field runs the class's
    /// static constructor, which calls <c>new ActivitySource(...)</c> and notifies already-registered
    /// listeners synchronously — so reading the field back from inside this callback observes it
    /// before assignment completes and throws.
    /// </remarks>
    private sealed class RecordedActivities : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly List<Activity> _collected = [];

        public RecordedActivities()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _collected.Add
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public List<Activity> For(string domain) =>
            _collected.Where(a => a.DisplayName == $"Discovery.Resolve/{domain}").ToList();

        public void Dispose()
        {
            _listener.Dispose();
            Activity.Current = null;
        }
    }

    private static HttpDomainDiscoveryProvider CreateProvider(CachingDiscoveryRegistryClient client) =>
        new(client,
            Options.Create(new ServiceDiscoveryOptions
            {
                Enabled = true,
                BaseUrl = BaseUrl,
                Domain = "discovery",
                Cache = new DiscoveryCacheOptions { Enabled = true }
            }),
            NullLogger<HttpDomainDiscoveryProvider>.Instance);

    // ────────────────────────────────────────────────────────────────────
    // Harness
    // ────────────────────────────────────────────────────────────────────

    private static CachingDiscoveryRegistryClient CreateSut(
        out DomainDiscoveryResolverTests.RoutingHandler handler,
        out FakeDistributedCache store,
        out FakeClock clock,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        Func<string>? baseUrlProvider = null,
        bool discoveryEnabled = true,
        int delayMilliseconds = 0,
        string domain = Domain,
        Action<DiscoveryCacheOptions>? configureCache = null)
    {
        baseUrlProvider ??= () => "https://lending.test";

        handler = new DomainDiscoveryResolverTests.RoutingHandler(
            respond ?? (_ =>
            {
                if (delayMilliseconds > 0)
                    Thread.Sleep(delayMilliseconds);

                return SuccessResponse(baseUrlProvider(), domain);
            }));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        var client = new HttpClient(handler);
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(client);

        var cacheOptions = new DiscoveryCacheOptions { Enabled = true };
        configureCache?.Invoke(cacheOptions);

        var options = Options.Create(new ServiceDiscoveryOptions
        {
            Enabled = discoveryEnabled,
            BaseUrl = BaseUrl,
            Domain = "discovery",
            Cache = cacheOptions
        });

        store = new FakeDistributedCache();
        clock = new FakeClock();

        return new CachingDiscoveryRegistryClient(
            new DiscoveryRegistryClient(httpClientFactory, options, NullLogger<DiscoveryRegistryClient>.Instance),
            store,
            new DiscoveryL1Cache(options),
            options,
            clock,
            NullLogger<CachingDiscoveryRegistryClient>.Instance);
    }

    private static HttpResponseMessage SuccessResponse(string baseUrl, string domain = Domain) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                  {
                    "data": {
                      "domainName": "{{domain}}",
                      "baseUrl": "{{baseUrl}}",
                      "appId": "lending-app",
                      "healthUrl": "{{baseUrl}}/health"
                    },
                    "eTag": "\"01ABC\""
                  }
                  """,
                System.Text.Encoding.UTF8,
                "application/json")
        };

    /// <summary>
    /// A cache that never expires anything on its own — deliberately, so the tests measure the
    /// client's own age check rather than a store TTL that production may not honour.
    /// </summary>
    internal sealed class FakeDistributedCache : IDistributedCacheService
    {
        public ConcurrentDictionary<string, object> Entries { get; } = new();
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        {
            if (FailReads)
                throw new InvalidOperationException("cache unavailable");

            return Task.FromResult(Entries.TryGetValue(key, out var value) ? value as T : null);
        }

        public Task SetAsync<T>(
            string key,
            T value,
            DistributedCacheEntryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            if (FailWrites)
                throw new InvalidOperationException("cache unavailable");

            Entries[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Entries.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<T?> GetOrSetAsync<T>(
            string cacheKey,
            Func<Task<T>> fetchFunc,
            DistributedCacheEntryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
        {
            var existing = await GetAsync<T>(cacheKey, cancellationToken);
            if (existing is not null)
                return existing;

            var fetched = await fetchFunc();
            await SetAsync(cacheKey, fetched, options, cancellationToken);
            return fetched;
        }

        public Task<T?> GetOrSetAsync<TKey, T>(
            TKey key,
            Func<TKey, Task<T>> fetchFunc,
            Func<TKey, string>? keySelector = null,
            DistributedCacheEntryOptions? options = null,
            CancellationToken cancellationToken = default) where T : class
            => GetOrSetAsync(keySelector?.Invoke(key) ?? key!.ToString()!, () => fetchFunc(key), options, cancellationToken);
    }

    internal sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
