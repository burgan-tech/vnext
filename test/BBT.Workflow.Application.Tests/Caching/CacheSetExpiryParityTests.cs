using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Issue #1192: after a publish, new instances started on the new package build until the cached answers
/// expired, then on older builds — the cold resolution chose from a version list that had lost its newest
/// rows. Pins that a floating request gets the same build from the entries the publish warmed and from a
/// cold resolution once every entry has expired: the highest package build without a pointer, the
/// pointer's build for <c>latest</c> and floating spellings with one, and the highest build for an explicit
/// artifact request, which the pointer never caps.
/// </summary>
public sealed class CacheSetExpiryParityTests
{
    private const string Domain = CacheSetTestHarness.TestDomain;
    private const string Key = CacheSetTestHarness.TestKey;

    private const string Build345 = "1.0.0-pkg.1.0.345+contract";
    private const string Build349 = "1.0.0-pkg.1.0.349+contract";
    private const string Build350 = "1.0.0-pkg.1.0.350+contract";

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1.0.0")]
    [InlineData("1.0")]
    [InlineData("1")]
    public async Task WithoutAPointer_TheWarmAndTheColdAnswerAreBothTheNewestBuild(string? requested)
    {
        var harness = await PublishBuild350Async(versionPolicy: null);

        var warm = await harness.Sut.GetByVersionAsync(Domain, Key, requested);
        harness.Backend.VersionListCallCount.ShouldBe(0, "the publish warmed this spelling");

        ExpireEveryEntry(harness);
        var cold = await harness.Sut.GetByVersionAsync(Domain, Key, requested);

        harness.Backend.VersionListCallCount.ShouldBe(1, "the expired answer is resolved again from the database");
        warm.Value!.Version.ShouldBe(Build350);
        cold.Value!.Version.ShouldBe(Build350);
    }

    [Theory]
    [InlineData(null, Build349)]
    [InlineData("latest", Build349)]
    [InlineData("1.0", Build349)]
    [InlineData("1", Build349)]
    [InlineData("1.0.0", Build350)]
    public async Task WithAPointer_TheWarmAndTheColdAnswerAgree(string? requested, string expected)
    {
        var policy = new CacheSetTestHarness.FakeVersionPolicySource { Pointer = "1.0.0-pkg.1.0.349" };
        var harness = await PublishBuild350Async(policy);

        var warm = await harness.Sut.GetByVersionAsync(Domain, Key, requested);
        harness.Backend.VersionListCallCount.ShouldBe(0, "the publish warmed this spelling");

        ExpireEveryEntry(harness);
        var cold = await harness.Sut.GetByVersionAsync(Domain, Key, requested);

        harness.Backend.VersionListCallCount.ShouldBe(1, "the expired answer is resolved again from the database");
        warm.Value!.Version.ShouldBe(expected);
        cold.Value!.Version.ShouldBe(expected);
    }

    /// <summary>
    /// Builds 345 and 349 are stored; 350 is published through <see cref="CacheSet{T}.SetAsync"/>, which
    /// warms the common spellings. Backend counters are reset afterwards, so a test sees only its own reads.
    /// </summary>
    private static async Task<CacheSetTestHarness> PublishBuild350Async(IVersionPolicySource? versionPolicy)
    {
        var harness = new CacheSetTestHarness(versionPolicy: versionPolicy);
        harness.Publish(Build345, Build349);

        var published = CacheSetTestHarness.CreateView(Build350);
        harness.AddPublished(published.Version);
        (await harness.Sut.SetAsync(published)).IsSuccess.ShouldBeTrue();

        harness.Backend.ResetCounts();
        return harness;
    }

    /// <summary>Moves past every TTL — generation token, resolution entries, full-version bodies, L1.</summary>
    private static void ExpireEveryEntry(CacheSetTestHarness harness)
    {
        var longest = Math.Max(
            Math.Max(harness.Options.GenerationTtlSeconds, harness.Options.ResolutionTtlSeconds),
            harness.Options.FullVersionTtlSeconds);
        harness.Time.Advance(TimeSpan.FromSeconds(longest + 1));
    }
}
