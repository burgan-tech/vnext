using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Behavior of the bytes-mode in-process envelope cache in isolation: round-trip, per-read instance
/// isolation, the negative-envelope guard, and the disabled mode being inert.
/// </summary>
public class ComponentL1CacheTests
{
    private static ComponentL1Cache Create(Action<ComponentCacheOptions>? configure = null)
    {
        var options = new ComponentCacheOptions();
        configure?.Invoke(options);
        return new ComponentL1Cache(Microsoft.Extensions.Options.Options.Create(options));
    }

    private static CacheEnvelope<View> Envelope(string version = "1.0.0-pkg.1.0.0")
    {
        var view = CacheSetTestHarness.CreateView(version);
        return new CacheEnvelope<View>
        {
            Domain = view.Domain,
            Key = view.Key,
            Version = view.Version,
            Flow = view.ComponentKey,
            Entity = view
        };
    }

    private static DateTimeOffset FutureExpiry => DateTimeOffset.UtcNow.AddMinutes(30);

    [Fact]
    public void Set_then_TryGet_round_trips_the_envelope()
    {
        using var sut = Create();
        sut.Set("k1", Envelope(), FutureExpiry);

        var got = sut.TryGet<View>("k1");

        got.ShouldNotBeNull();
        got.Version.ShouldBe("1.0.0-pkg.1.0.0");
        got.Entity.ShouldNotBeNull();
    }

    [Fact]
    public void TryGet_returns_a_fresh_instance_per_read()
    {
        using var sut = Create();
        sut.Set("k1", Envelope(), FutureExpiry);

        var first = sut.TryGet<View>("k1");
        var second = sut.TryGet<View>("k1");

        ReferenceEquals(first!.Entity, second!.Entity).ShouldBeFalse();
    }

    [Fact]
    public void Negative_envelopes_are_never_stored()
    {
        using var sut = Create();
        sut.Set("k1", new CacheEnvelope<View> { Domain = "core", Key = "k", IsNegative = true }, FutureExpiry);

        sut.TryGet<View>("k1").ShouldBeNull();
    }

    [Fact]
    public void Remove_evicts_the_entry()
    {
        using var sut = Create();
        sut.Set("k1", Envelope(), FutureExpiry);

        sut.Remove("k1");

        sut.TryGet<View>("k1").ShouldBeNull();
    }

    [Fact]
    public void Disabled_cache_stores_nothing_and_never_throws()
    {
        using var sut = Create(o => o.L1Enabled = false);
        sut.Set("k1", Envelope(), FutureExpiry);

        sut.TryGet<View>("k1").ShouldBeNull();
        sut.Remove("k1");
    }

    [Fact]
    public void Miss_returns_null()
    {
        using var sut = Create();
        sut.TryGet<View>("absent").ShouldBeNull();
    }
}
