using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static BBT.Workflow.Caching.CacheSetTestHarness;

namespace BBT.Workflow.Caching;

/// <summary>
/// Unit tests for <see cref="CacheSet{T}"/> version resolution once the publish-time (warmed) entries
/// have expired and the answer comes from the backend's candidate list instead (issue #1192).
/// </summary>
/// <remarks>
/// The report: after a publish of <c>…350</c>, new instances started on <c>…350</c> for a while, then
/// on <c>…349</c> (latest) and <c>…345</c> (<c>"1.0.0"</c>) while the stored definition stayed on
/// <c>…350</c>. The resolver picks the highest match among the versions it is given, so these tests pin
/// three things: a complete candidate list keeps the same answer across expiry; a candidate list that
/// lacks the newest build is exactly what produces an older answer; and a candidate list the backend
/// knows to be partial is never cached for the full resolution TTL.
/// </remarks>
public class CacheSetResolutionExpiryTests
{
    private const string V345 = "1.0.0-pkg.1.0.345+contract";
    private const string V349 = "1.0.0-pkg.1.0.349+contract";
    private const string V350 = "1.0.0-pkg.1.0.350+contract";

    private static readonly string?[] FloatingSpellings = [null, "latest", "1.0.0", "1.0", "1"];

    private static TimeSpan PastEveryResolutionEntry(CacheSetTestHarness harness)
        => TimeSpan.FromSeconds(harness.Options.GenerationTtlSeconds + harness.Options.ResolutionTtlSeconds + 1);

    private static TimeSpan PastNegativeEntry(CacheSetTestHarness harness)
        => TimeSpan.FromSeconds(Math.Max(harness.Options.NegativeTtlSeconds, harness.Options.GenerationMemoSeconds) + 1);

    // ────────────────────────────────────────────────────────────────────
    // Warm and cold agree for the same database state
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expiry_ShouldResolveToHighestPackageBuild_BeforeAndAfterWarmEntriesExpire()
    {
        // Arrange - three package builds of one artifact, the newest just published and warmed.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349, V350);
        await harness.Sut.SetAsync(CreateView(V350));

        foreach (var spelling in FloatingSpellings)
            (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, spelling)).Value!.Version.ShouldBe(V350);

        // Act - every generation token and resolution entry expires.
        harness.Time.Advance(PastEveryResolutionEntry(harness));
        harness.Backend.ResetCounts();

        // Assert - the cold path gives the same answer the warm one did.
        foreach (var spelling in FloatingSpellings)
        {
            var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, spelling);
            result.IsSuccess.ShouldBeTrue();
            result.Value!.Version.ShouldBe(V350, $"spelling '{spelling ?? "<null>"}' after expiry");
        }

        harness.Backend.LoadAllCallCount.ShouldBeGreaterThan(0, "the answers must come from the backend path");
    }

    [Fact]
    public async Task Expiry_ShouldResolveToHighestPackageBuild_RegardlessOfPublishOrder()
    {
        // Arrange - the newest build was stored first, an older one was (re)published last.
        var harness = new CacheSetTestHarness();
        harness.Publish(V350, V349, V345);
        await harness.Sut.SetAsync(CreateView(V345));

        // Act
        harness.Time.Advance(PastEveryResolutionEntry(harness));

        // Assert
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V350);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null)).Value!.Version.ShouldBe(V350);
    }

    [Fact]
    public async Task Expiry_WhenBackendCannotSeeNewestBuild_ColdPathAnswersFromWhatTheBackendReturns()
    {
        // Arrange - the mechanism behind #1192: the publish warms with the published entity added to the
        // candidates, but the backend's own list does not contain it.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349);
        await harness.Sut.SetAsync(CreateView(V350));

        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V350);

        // Act
        harness.Time.Advance(PastEveryResolutionEntry(harness));

        // Assert - once the warm entry is gone, the answer is the best of what the backend returned.
        // An older answer therefore always means the newer build was missing from the candidate list,
        // which is what the resolution log and span now record.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V349);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest")).Value!.Version.ShouldBe(V349);
    }

    // ────────────────────────────────────────────────────────────────────
    // A partial candidate list is never pinned for the full TTL
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Resolve_WithDroppedCandidates_ShouldStillAnswer()
    {
        // Arrange - the newest stored build cannot be loaded.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349);
        harness.Backend.DroppedVersions = [V350];

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");

        // Assert - one unreadable row must not turn into an outage of the component.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe(V349);
    }

    [Fact]
    public async Task Resolve_WithDroppedCandidates_ShouldReResolveAfterNegativeTtl()
    {
        // Arrange - resolved while the newest build was unreadable.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349);
        harness.Backend.DroppedVersions = [V350];
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V349);

        // Act - the row becomes readable again, with no publish and no generation bump.
        harness.AddPublished(V350);
        harness.Backend.DroppedVersions = [];
        harness.Time.Advance(PastNegativeEntry(harness));
        harness.Backend.ResetCounts();

        // Assert - the short-lived answer expired and the full list now wins.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V350);
        harness.Backend.LoadAllCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_WithCompleteCandidates_ShouldKeepFullResolutionTtl()
    {
        // Arrange - the control case: nothing dropped, so the answer is cached for the resolution TTL.
        var harness = new CacheSetTestHarness();
        harness.Publish(V345, V349);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V349);

        // Act - the same short wait as above; the backend changes without a publish.
        harness.AddPublished(V350);
        harness.Time.Advance(PastNegativeEntry(harness));
        harness.Backend.ResetCounts();

        // Assert - still the cached answer, with no backend load: the short TTL applies only to a
        // partial candidate list, so the normal hit path is unchanged.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V349);
        harness.Backend.LoadAllCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Resolve_WithOnlyDroppedCandidates_ShouldFailAsNotFound()
    {
        // Arrange - every stored version is unreadable.
        var harness = new CacheSetTestHarness();
        harness.Backend.DroppedVersions = [V350];

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0");

        // Assert
        result.IsSuccess.ShouldBeFalse();
    }

    // ────────────────────────────────────────────────────────────────────
    // Default-version pointer (existing contract, pinned across expiry)
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expiry_WithPointerOnOlderBuild_ShouldKeepLatestOnPointerAndArtifactOnHighest()
    {
        // Arrange - a full pointer at …349; publish never moves it.
        var policy = new FakeVersionPolicySource { Pointer = V349 };
        var harness = new CacheSetTestHarness(versionPolicy: policy);
        harness.Publish(V345, V349, V350);
        await harness.Sut.SetAsync(CreateView(V350));

        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null)).Value!.Version.ShouldBe(V349);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V350);

        // Act
        harness.Time.Advance(PastEveryResolutionEntry(harness));

        // Assert - latest follows the pointer; an artifact request is explicit and is not capped by it.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, null)).Value!.Version.ShouldBe(V349);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.0.0")).Value!.Version.ShouldBe(V350);
    }
}
