using System;
using System.Collections.Generic;
using BBT.Workflow.Discovery;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    public void The_default_window_is_one_hour_with_a_two_hour_dead_mans_switch()
    {
        var options = new DiscoveryCacheOptions();

        // Sized to how often the data actually changes: a domain's registered address moves when that
        // domain is deployed to a new address — rare and planned, not continuous drift. Recorded as a
        // test so the intent survives someone later "tightening" it back to minutes without knowing
        // that the forced-refresh endpoint, not a short window, is what covers the moving case.
        options.RefreshIntervalSeconds.ShouldBe(3600);

        // Twice the window: survive one missed refresh, expire after roughly two.
        options.L2TtlSeconds.ShouldBe(options.RefreshIntervalSeconds * 2);

        // Short despite the long window — it is the residual staleness after a forced refresh.
        options.L1TtlSeconds.ShouldBe(60);

        // Also short: a tick inside a served window costs one cache read, and what it buys is
        // retrying a FAILED window within a minute rather than at the end of the hour.
        options.TickIntervalSeconds.ShouldBe(60);
        options.TickIntervalSeconds.ShouldBeLessThan(options.RefreshIntervalSeconds);
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
    public void An_empty_accepted_status_set_is_rejected()
    {
        var result = Validate(new DiscoveryCacheOptions
        {
            Enabled = true,
            AcceptedStatuses = []
        });

        // Would warm nothing at all while looking perfectly healthy.
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("AcceptedStatuses");
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
