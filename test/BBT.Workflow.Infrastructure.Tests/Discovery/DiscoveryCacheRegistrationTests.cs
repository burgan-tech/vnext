using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using BBT.Workflow.HostedServices;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins the registration rules that scope the discovery cache: default provider only, and off by
/// default.
/// </summary>
/// <remarks>
/// Both are enforced where the client is REGISTERED rather than by a flag the decorator checks at
/// read time, and the difference is not stylistic. A read-time flag would leave an entry written
/// before the flip still readable after it, so <c>Cache:Enabled=false</c> would only approximate the
/// pre-cache behaviour instead of restoring it. These tests are what keep that property true.
/// </remarks>
public sealed class DiscoveryCacheRegistrationTests
{
    [Fact]
    public void Cache_disabled_registers_the_plain_registry_client()
    {
        var provider = Build(cacheEnabled: false, discoveryProvider: "http");

        provider.GetRequiredService<IDiscoveryRegistryClient>().ShouldBeOfType<DiscoveryRegistryClient>();
        provider.GetService<IDiscoveryCacheWriter>().ShouldBeNull();
    }

    [Fact]
    public void Cache_enabled_on_the_default_provider_registers_the_caching_client()
    {
        var provider = Build(cacheEnabled: true, discoveryProvider: "http");

        provider.GetRequiredService<IDiscoveryRegistryClient>()
            .ShouldBeOfType<CachingDiscoveryRegistryClient>();

        // The refresher writes through the same object that serves reads, so the two cannot drift
        // apart on key format.
        provider.GetRequiredService<IDiscoveryCacheWriter>()
            .ShouldBeSameAs(provider.GetRequiredService<IDiscoveryRegistryClient>());
    }

    [Fact]
    public void Dapr_provider_never_gets_the_caching_client_even_when_the_cache_is_enabled()
    {
        var provider = Build(cacheEnabled: true, discoveryProvider: "dapr");

        // The scope decision. Under "dapr" the app-id comes from a naming convention and the common
        // path makes no network call at all, so there is nothing to win — while the provider's own
        // in-process app-id cache would become a second TTL stacked on this one, and a staleness
        // window built from two multiplying TTLs is one nobody can reason about mid-incident.
        provider.GetRequiredService<IDiscoveryRegistryClient>().ShouldBeOfType<DiscoveryRegistryClient>();
        provider.GetService<IDiscoveryCacheWriter>().ShouldBeNull();
        provider.GetRequiredService<IDomainDiscoveryResolver>().ShouldBeOfType<DaprDomainDiscoveryProvider>();
    }

    [Fact]
    public void An_unrecognised_provider_falls_back_to_http_and_still_gets_the_cache()
    {
        var provider = Build(cacheEnabled: true, discoveryProvider: "htpp");

        // Same existing rule as transport selection: a typo must not move traffic, and it must not
        // quietly disable the cache either.
        provider.GetRequiredService<IDomainDiscoveryResolver>().ShouldBeOfType<HttpDomainDiscoveryProvider>();
        provider.GetRequiredService<IDiscoveryRegistryClient>().ShouldBeOfType<CachingDiscoveryRegistryClient>();
    }

    [Fact]
    public void The_cache_is_off_by_default()
    {
        // This reverses a shipped decision, and domain teams consuming the runtime as a package
        // inherit code defaults. Default-on would silently reintroduce a staleness window for every
        // one of them on upgrade.
        new DiscoveryCacheOptions().Enabled.ShouldBeFalse();
    }

    // ────────────────────────────────────────────────────────────────────
    // Invariants
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_defaults_are_expiry_free_and_event_driven()
    {
        var options = new DiscoveryCacheOptions();

        // No periodic refresh and no expiry. The cached datum — where a domain answers — changes when
        // that domain is deployed, so a timer can only fire when nothing changed or fire too late;
        // what it used to cost was a ~70 minute window routing to a moved domain's old address.
        // Recorded as a test so a later "let's just put an hour back" has to argue with the reasoning
        // rather than with a number.
        options.RefreshIntervalSeconds.ShouldBe(0);
        options.L2TtlSeconds.ShouldBe(0);

        // What replaces the dead-man TTL: the entry is dropped when something fails to reach it.
        options.UnreachableEvictionCooldownSeconds.ShouldBe(30);

        // One minute, down from the ten it carried while it had to stay under an hour-long window.
        // This is how long an invalidation takes to reach the pods that did not perform it, and with
        // no window to fit inside, the only thing a longer value buys is one saved cache read.
        options.L1TtlSeconds.ShouldBe(60);

        // Purely a retry cadence now: the loop stops once the cluster's cache is filled.
        options.TickIntervalSeconds.ShouldBe(60);
    }

    [Fact]
    public void Default_options_satisfy_their_own_invariants()
    {
        var result = Validate(new DiscoveryCacheOptions { Enabled = true });

        result.Failed.ShouldBeFalse(result.FailureMessage);
    }

    [Fact]
    public void A_refresh_interval_at_or_above_the_entry_age_is_rejected()
    {
        // The failure this catches is invisible at runtime: entries expire between refreshes, so
        // every domain's first caller after each expiry pays full registry latency and the cache
        // quietly stops earning its keep. Nothing logs, nothing errors.
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            RefreshIntervalSeconds = 300,
            L2TtlSeconds = 180
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("RefreshIntervalSeconds");
    }

    [Fact]
    public void An_l1_ttl_at_or_above_the_refresh_interval_is_rejected()
    {
        // L1 is the only layer the refresher cannot overwrite, so an oversized L1 TTL is a pod
        // serving an address the cluster has already corrected.
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            L1TtlSeconds = 120,
            RefreshIntervalSeconds = 60
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("L1TtlSeconds");
    }

    [Fact]
    public void A_lease_longer_than_the_refresh_window_is_rejected()
    {
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            WarmupLockLeaseSeconds = 90,
            RefreshIntervalSeconds = 60
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("WarmupLockLeaseSeconds");
    }

    [Fact]
    public void An_empty_domain_list_endpoint_template_is_rejected()
    {
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            DomainListEndpointTemplate = string.Empty
        });

        // The bulk read is the only thing that fills the cache; with nowhere to read from it warms
        // nothing at all while looking perfectly healthy.
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("DomainListEndpointTemplate");
    }

    [Fact]
    public void A_disabled_cache_is_never_validated()
    {
        // Nonsense values behind a disabled switch must not stop a host from booting.
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = false,
            L1TtlSeconds = 9999,
            RefreshIntervalSeconds = 1,
            L2TtlSeconds = 1
        });

        result.Failed.ShouldBeFalse();
    }

    // ────────────────────────────────────────────────────────────────────
    // The refresh loop must agree with the registration above
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_refresh_loop_does_not_start_when_no_refresher_was_registered()
    {
        // Cache:Enabled = true, provider = dapr: the registration deliberately skips the refresher
        // (the test above pins that), so there is nothing for the loop to drive.
        var provider = Build(cacheEnabled: true, discoveryProvider: "dapr");

        provider.GetService<IDiscoveryCacheRefresher>().ShouldBeNull();
        BuildRefreshService(provider).IsRefresherRegistered().ShouldBeFalse();
    }

    [Fact]
    public void The_refresh_loop_starts_when_a_refresher_was_registered()
    {
        var provider = Build(cacheEnabled: true, discoveryProvider: "http");

        BuildRefreshService(provider).IsRefresherRegistered().ShouldBeTrue();
    }

    [Fact]
    public async Task A_tick_without_a_refresher_is_survivable_rather_than_fatal()
    {
        // Belt and braces for the probe: even if something ever starts the loop against a container
        // with no refresher, the tick must not escape. It used to escape into the tick's own
        // catch-all and log an InvalidOperationException EVERY tick, forever — 132 occurrences in one
        // lab pod under Provider = "dapr" with the default Cache:Enabled = true, because the service
        // spelled the registration's condition a second time (Enabled && Cache.Enabled) and the two
        // drifted. The probe is the fix; this pins that the failure mode stays non-fatal regardless.
        var provider = Build(cacheEnabled: true, discoveryProvider: "dapr");

        await BuildRefreshService(provider).TickAsync(CancellationToken.None);
    }

    [Fact]
    public void An_expiry_with_no_periodic_refresh_is_rejected()
    {
        // The one combination that is broken in both modes: entries die on their own schedule and
        // nothing renews them, so every domain's next caller pays a live lookup — forever, with the
        // cache still reporting hits in between. Event-driven invalidation cannot cover for a TTL
        // because it does not renew anything on a timer.
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            RefreshIntervalSeconds = 0,
            L2TtlSeconds = 7200
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("L2TtlSeconds");
    }

    [Fact]
    public void The_window_invariants_do_not_apply_when_there_is_no_window()
    {
        // In event-driven mode L1TtlSeconds, the tick and the lease have no window to be smaller
        // than. Applying the old relationships to a zero would reject the shipped defaults.
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            RefreshIntervalSeconds = 0,
            L2TtlSeconds = 0,
            L1TtlSeconds = 600,
            WarmupLockLeaseSeconds = 30,
            TickIntervalSeconds = 60
        });

        result.Failed.ShouldBeFalse(result.FailureMessage);
    }

    // ────────────────────────────────────────────────────────────────────
    // Warm-up-only loop
    // ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiscoveryCacheRefreshOutcome.Refreshed)]
    [InlineData(DiscoveryCacheRefreshOutcome.SkippedWindowFresh)]
    public async Task With_no_refresh_interval_the_loop_stops_once_the_cluster_is_filled(
        DiscoveryCacheRefreshOutcome outcome)
    {
        var refresher = Substitute.For<IDiscoveryCacheRefresher>();
        refresher.RefreshAsync(false, Arg.Any<CancellationToken>()).Returns(outcome);

        var service = BuildLoopService(refresher, refreshIntervalSeconds: 0);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        // A pod that kept ticking would spend one marker read a minute, forever, to be told nothing
        // changed — and nothing can change on a timer once invalidation is event-driven.
        await refresher.Received(1).RefreshAsync(false, Arg.Any<CancellationToken>());

        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(DiscoveryCacheRefreshOutcome.Failed)]
    [InlineData(DiscoveryCacheRefreshOutcome.SkippedNotOwner)]
    public async Task With_no_refresh_interval_the_loop_keeps_retrying_until_the_cluster_is_filled(
        DiscoveryCacheRefreshOutcome outcome)
    {
        var refresher = Substitute.For<IDiscoveryCacheRefresher>();
        var firstCall = new TaskCompletionSource();
        refresher.RefreshAsync(false, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            firstCall.TrySetResult();
            return outcome;
        });

        var service = BuildLoopService(refresher, refreshIntervalSeconds: 0);

        await service.StartAsync(CancellationToken.None);
        await firstCall.Task;

        // Neither outcome means the cache is filled: a failed read leaves it cold, and a replica that
        // merely holds the lock may still fail — exiting here would leave this pod resolving live for
        // the rest of its life.
        service.ExecuteTask!.IsCompleted.ShouldBeFalse();

        await service.StopAsync(CancellationToken.None);
    }

    private static DiscoveryCacheRefreshHostedService BuildLoopService(
        IDiscoveryCacheRefresher refresher,
        int refreshIntervalSeconds)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(refresher);
        var provider = services.BuildServiceProvider();

        var options = Options.Create(new ServiceDiscoveryOptions
        {
            Enabled = true,
            Cache = new DiscoveryCacheOptions
            {
                Enabled = true,
                RefreshIntervalSeconds = refreshIntervalSeconds,
                TickIntervalSeconds = 60,
                L2TtlSeconds = refreshIntervalSeconds > 0 ? refreshIntervalSeconds * 2 : 0
            }
        });

        return new DiscoveryCacheRefreshHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            provider.GetRequiredService<ILogger<DiscoveryCacheRefreshHostedService>>());
    }

    private static DiscoveryCacheRefreshHostedService BuildRefreshService(ServiceProvider provider)
        => new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<ServiceDiscoveryOptions>>(),
            TimeProvider.System,
            provider.GetRequiredService<ILogger<DiscoveryCacheRefreshHostedService>>());

    // ────────────────────────────────────────────────────────────────────
    // Harness
    // ────────────────────────────────────────────────────────────────────

    private static ValidateOptionsResult Validate(DiscoveryCacheOptions cache)
        => new ServiceDiscoveryOptionsValidator().Validate(
            Options.DefaultName,
            new ServiceDiscoveryOptions { Cache = cache });

    private static ServiceProvider Build(bool cacheEnabled, string discoveryProvider)
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServiceDiscovery:Enabled"] = "true",
                ["ServiceDiscovery:BaseUrl"] = "https://discovery.test/api/v1",
                ["ServiceDiscovery:Domain"] = "discovery",
                ["ServiceDiscovery:Provider"] = discoveryProvider,
                ["ServiceDiscovery:Cache:Enabled"] = cacheEnabled ? "true" : "false"
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());
        services.AddSingleton(Substitute.For<BBT.Aether.DistributedCache.IDistributedCacheService>());

        services.AddDomainDiscovery();

        return services.BuildServiceProvider();
    }
}
