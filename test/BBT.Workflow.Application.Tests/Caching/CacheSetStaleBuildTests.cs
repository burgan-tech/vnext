using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static BBT.Workflow.Caching.CacheSetTestHarness;

namespace BBT.Workflow.Caching;

/// <summary>
/// Regression tests for #1192: hours after a publish, new instances resolved to an older package build
/// once the warmed resolution entries expired and the answer was computed from the backend alone.
/// </summary>
/// <remarks>
/// The comparison itself is not the suspect — <c>"1.0.0"</c> and <c>latest</c> pick the highest package
/// build of whatever they are given. These tests pin that a cache miss after expiry gives the same answer
/// as the warmed entry for the same stored state, and that an answer computed while a stored version was
/// unreadable is never cached for the full resolution TTL.
/// </remarks>
public class CacheSetStaleBuildTests
{
    private const string V345 = "1.0.0-pkg.1.0.345+contract";
    private const string V349 = "1.0.0-pkg.1.0.349+contract";
    private const string V350 = "1.0.0-pkg.1.0.350+contract";

    /// <summary>Far past both the generation and the resolution TTL (3600s by default).</summary>
    private static readonly TimeSpan PastEveryTtl = TimeSpan.FromHours(3);

    [Theory]
    [InlineData(null)]
    [InlineData("latest")]
    [InlineData("1.0.0")]
    [InlineData("1.0")]
    [InlineData("1")]
    public async Task Resolve_AfterWarmEntriesExpire_ShouldStayOnHighestPackageBuild(string? requested)
    {
        // Arrange — three builds stored, the newest just published and warmed.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349, V350);
        (await harness.Sut.SetAsync(CreateView(V350))).IsSuccess.ShouldBeTrue();

        var warm = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, requested);
        warm.Value!.Version.ShouldBe(V350);

        // Act — every warmed entry and the generation token expire; the next read is a cold miss.
        harness.Time.Advance(PastEveryTtl);
        harness.Backend.ResetCounts();
        var cold = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, requested);

        // Assert — the backend was consulted and agreed with the warmed answer.
        harness.Backend.LoadAllCallCount.ShouldBe(1);
        cold.IsSuccess.ShouldBeTrue();
        cold.Value!.Version.ShouldBe(V350);
    }

    [Fact]
    public async Task Resolve_WithDroppedStoredVersion_ShouldReturnBestReadableButCacheItBriefly()
    {
        // Arrange — 350 is stored but cannot be read, so only 345 and 349 are candidates.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349);
        harness.Backend.DroppedVersions = [V350];

        // Act
        var degraded = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");

        // Assert — the read still succeeds (failing it would turn a degraded answer into an outage)…
        degraded.IsSuccess.ShouldBeTrue();
        degraded.Value!.Version.ShouldBe(V349);

        // …but the resolution entry lives only as long as a negative entry, not the resolution TTL.
        var generation = await harness.CurrentGenerationAsync();
        var expiry = harness.Cache.ExpiryOf(harness.ResolutionKey(generation, "1.0.0"));
        expiry.ShouldNotBeNull();
        expiry.Value.ShouldBe(harness.Time.GetUtcNow().AddSeconds(harness.Options.NegativeTtlSeconds));

        // The row becomes readable again; once the short entry expires the next read sees 350.
        harness.Backend.DroppedVersions = [];
        harness.AddPublished(V350);
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Options.NegativeTtlSeconds + 1));

        var recovered = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");
        recovered.Value!.Version.ShouldBe(V350);
    }

    [Fact]
    public async Task Resolve_WithDroppedStoredVersion_ShouldNotOutliveShortTtlInL1()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349);
        harness.Backend.DroppedVersions = [V350];
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V349);

        // A second read inside the short window is a cache hit, not another backend load.
        harness.Backend.ResetCounts();
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V349);
        harness.Backend.LoadAllCallCount.ShouldBe(0);

        // Act — the row recovers and the short window passes.
        harness.Backend.DroppedVersions = [];
        harness.AddPublished(V350);
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Options.NegativeTtlSeconds + 1));
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");

        // Assert — neither L2 nor L1 kept the degraded answer.
        harness.Backend.LoadAllCallCount.ShouldBe(1);
        result.Value!.Version.ShouldBe(V350);
    }

    [Fact]
    public async Task Resolve_WithoutDroppedVersions_ShouldCacheForResolutionTtl()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349, V350);

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");

        // Assert — a complete candidate set keeps the ordinary TTL.
        result.Value!.Version.ShouldBe(V350);
        var generation = await harness.CurrentGenerationAsync();
        harness.Cache.ExpiryOf(harness.ResolutionKey(generation, "1.0.0"))
            .ShouldBe(harness.Time.GetUtcNow().AddSeconds(harness.Options.ResolutionTtlSeconds));
    }

    [Fact]
    public async Task Resolve_WhenEveryStoredVersionIsDropped_ShouldFailAsNotFound()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish();
        harness.Backend.DroppedVersions = [V350];

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");

        // Assert — unchanged outcome: nothing readable is nothing found (short negative entry).
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Resolve_WithPointerOnOlderBuild_LatestFollowsPointerAndArtifactIgnoresIt()
    {
        // Arrange — the default-version pointer was set to 349 before 350 was published. Publishing
        // does not move it, so latest stays on 349; an artifact request is explicit and is not capped.
        var policy = new FakeVersionPolicySource { Pointer = V349 };
        var harness = new CacheSetTestHarness(versionPolicy: policy);
        harness.Publish(V345, V349, V350);
        (await harness.Sut.SetAsync(CreateView(V350))).IsSuccess.ShouldBeTrue();

        // Act / Assert — warm and cold agree for both spellings.
        foreach (var advance in new[] { TimeSpan.Zero, PastEveryTtl })
        {
            harness.Time.Advance(advance);

            (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null)).Value!.Version.ShouldBe(V349);
            (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V350);
        }
    }

    [Fact]
    public void FormatVersions_ShouldOrderAndBoundTheList()
    {
        var versions = Enumerable.Range(0, CacheSet<Definitions.View>.MaxLoggedVersions + 5)
            .Select(i => $"1.0.0-pkg.1.0.{i:D3}")
            .Reverse()
            .ToList();

        var formatted = CacheSet<Definitions.View>.FormatVersions(versions);

        formatted.ShouldStartWith("1.0.0-pkg.1.0.000, ");
        formatted.ShouldEndWith(", … (+5)");
        formatted.Split(", ").Length.ShouldBe(CacheSet<Definitions.View>.MaxLoggedVersions + 1);
    }
}
