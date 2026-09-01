using System;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using static BBT.Workflow.Caching.CacheSetTestHarness;

namespace BBT.Workflow.Caching;

/// <summary>
/// Unit tests for <see cref="CacheSet{T}"/>, focused on version resolution staying correct across
/// publishes.
/// </summary>
/// <remarks>
/// The bug these were written for: a request for <c>"1"</c> was cached under a key derived from the
/// request string with no expiration, and publishing a newer 1.x never touched it — so the cache served
/// 1.5.0 forever after 1.6.0 shipped. The interesting assertions are therefore about which key a read
/// consults after a publish, and about how many backend loads that costs.
/// </remarks>
public class CacheSetTests
{
    private const string V150 = "1.5.0-pkg.1.0.0+core";
    private const string V151 = "1.5.1-pkg.1.0.0+core";
    private const string V160 = "1.6.0-pkg.1.0.0+core";

    // ────────────────────────────────────────────────────────────────────
    // The reported bug
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_NewerVersion_ShouldReplaceMajorOnlyResolution()
    {
        // Arrange - "1" resolves to 1.5.0 and is cached.
        var harness = new CacheSetTestHarness();
        harness.Publish(V150);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V150);

        // Act - 1.6.0 ships.
        harness.AddPublished(V160);
        await harness.Sut.SetAsync(CreateView(V160));

        // Assert
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe(V160);
    }

    [Fact]
    public async Task Publish_NewerVersion_ShouldReplaceMajorOnlyResolution_EvenWithoutWarming()
    {
        // Arrange - the same scenario as above, but with warming unavailable, so only invalidation can
        // produce the right answer. Without this the headline test above passes even with invalidation
        // removed, because warming happens to overwrite the same key.
        var harness = new CacheSetTestHarness();
        harness.Publish(V150);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V150);

        // Act
        harness.AddPublished(V160);
        harness.Backend.FailLoadAll = true;
        var publishResult = await harness.Sut.SetAsync(CreateView(V160));
        harness.Backend.FailLoadAll = false;

        // Assert
        publishResult.IsSuccess.ShouldBeTrue("a failed warm must not fail the publish");
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V160);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.6")]
    [InlineData("latest")]
    [InlineData(null)]
    public async Task Publish_NewerVersion_ShouldReplaceEveryRangeResolution(string? requested)
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150);
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, requested);

        // Act
        harness.AddPublished(V160);
        await harness.Sut.SetAsync(CreateView(V160));

        // Assert
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, requested);
        result.Value!.Version.ShouldBe(V160);
    }

    // ────────────────────────────────────────────────────────────────────
    // Publishes are not monotonic
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_OlderVersion_ShouldNotDisplaceLatest()
    {
        // Arrange - 1.6.0 is the winner and is cached as such.
        var harness = new CacheSetTestHarness();
        harness.Publish(V150, V160);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest")).Value!.Version.ShouldBe(V160);

        // Act - an emergency 1.5.1 is released after 1.6.0 already exists.
        harness.AddPublished(V151);
        await harness.Sut.SetAsync(CreateView(V151));

        // Assert - the published version is not automatically the winner of any range it belongs to.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest")).Value!.Version.ShouldBe(V160);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V160);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.5")).Value!.Version.ShouldBe(V151);
    }

    [Fact]
    public async Task Publish_LowerPackageVersion_ShouldNotDisplaceArtifactResolution()
    {
        // Arrange
        const string highPackage = "1.5.0-pkg.3.0.0+core";
        const string lowPackage = "1.5.0-pkg.2.0.0+core";

        var harness = new CacheSetTestHarness();
        harness.Publish(highPackage);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.5.0")).Value!.Version.ShouldBe(highPackage);

        // Act
        harness.AddPublished(lowPackage);
        await harness.Sut.SetAsync(CreateView(lowPackage));

        // Assert - the highest package version of an artifact wins, regardless of publish order.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.5.0")).Value!.Version.ShouldBe(highPackage);
    }

    [Fact]
    public async Task Publish_ShouldNotRankPackageVersionAboveArtifactVersion()
    {
        // Arrange - a lower artifact carrying a much higher package version must still lose.
        const string lowArtifactHighPackage = "1.5.0-pkg.9.9.9+core";

        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Act
        harness.AddPublished(lowArtifactHighPackage);
        await harness.Sut.SetAsync(CreateView(lowArtifactHighPackage));

        // Assert
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V160);
    }

    // ────────────────────────────────────────────────────────────────────
    // Generation scoping
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_ShouldChangeGenerationToken()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150);
        var before = await harness.CurrentGenerationAsync();

        // Act
        await harness.Sut.SetAsync(CreateView(V160));

        // Assert
        var after = await harness.CurrentGenerationAsync();
        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task Publish_ShouldInvalidateSpellingsNoPublishCouldEnumerate()
    {
        // Arrange - a package-version request. Nothing derivable from a published version's own
        // artifact/major/minor would ever name this spelling, which is why resolutions are scoped to a
        // generation rather than invalidated key by key.
        const string packageAliasRequest = "00.01.417";
        const string stored = "1.0.0-pkg.00.01.417+onboarding";

        var harness = new CacheSetTestHarness();
        harness.Publish(stored);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, packageAliasRequest)).Value!.Version.ShouldBe(stored);

        harness.Backend.ResetCounts();
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, packageAliasRequest)).IsSuccess.ShouldBeTrue();
        harness.Backend.LoadAllCallCount.ShouldBe(0, "the alias resolution should be cached");

        // Act
        harness.AddPublished(V160);
        await harness.Sut.SetAsync(CreateView(V160));
        harness.Backend.ResetCounts();
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, packageAliasRequest);

        // Assert - the previously cached alias answer is unreachable, so it was re-resolved.
        harness.Backend.LoadAllCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task GenerationExpiry_ShouldForceReResolution()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150);
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Act - the token TTL lapses without any publish having happened.
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Options.GenerationTtlSeconds + 1));
        harness.AddPublished(V160);
        harness.Backend.ResetCounts();

        // Assert - a fresh token means nothing cached under the old one is consulted.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V160);
        harness.Backend.LoadAllCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Publish_WhenGenerationWriteFails_ShouldRemoveTokenSoInvalidationStillHolds()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150);
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");
        var originalGeneration = await harness.CurrentGenerationAsync();

        // Act - the token write fails, which is the one path that could leave stale answers reachable.
        harness.Cache.FailWrites = key => key.EndsWith(":gen", StringComparison.Ordinal);
        harness.AddPublished(V160);
        await harness.Sut.SetAsync(CreateView(V160));
        harness.Cache.FailWrites = null;

        // Assert - falling back to removal invalidates just as effectively.
        harness.Cache.Removes.ShouldContain(harness.GenerationKey());
        harness.Cache.Keys.ShouldNotContain(harness.GenerationKey());

        var bootstrapped = await harness.CurrentGenerationAsync();
        bootstrapped.ShouldNotBe(originalGeneration);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V160);
    }

    // ────────────────────────────────────────────────────────────────────
    // Deactivation — the case a compare-and-swap could not fix
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Invalidate_AfterDeactivation_ShouldResolveToTheLowerVersion()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150, V160);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest")).Value!.Version.ShouldBe(V160);

        // Act - 1.6.0 turns out to be bad and is deactivated.
        harness.Deactivate(V160);
        await harness.Sut.InvalidateAsync(TestDomain, TestKey, V160);

        // Assert - resolution moves *down*, which is only possible because the answer is recomputed
        // rather than compared against what was already cached.
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest")).Value!.Version.ShouldBe(V150);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1")).Value!.Version.ShouldBe(V150);
    }

    [Fact]
    public async Task Invalidate_WithPartialVersion_ShouldNotRemoveTheFullVersionBody()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.Sut.SetAsync(CreateView(V160));
        harness.Cache.Keys.ShouldContain(harness.FullKey(V160));

        // Act - a range version names no single revision, so no body can be identified for removal.
        await harness.Sut.InvalidateAsync(TestDomain, TestKey, "1");

        // Assert
        harness.Cache.Keys.ShouldContain(harness.FullKey(V160));
    }

    [Fact]
    public async Task Invalidate_WithFullVersion_ShouldRemoveThatBody()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.Sut.SetAsync(CreateView(V160));

        // Act
        await harness.Sut.InvalidateAsync(TestDomain, TestKey, V160);

        // Assert
        harness.Cache.Keys.ShouldNotContain(harness.FullKey(V160));
    }

    // ────────────────────────────────────────────────────────────────────
    // Cost: the cache has to actually spare the database
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AfterPublish_CommonSpellingsShouldServeWithoutAnyBackendLoad()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.Sut.SetAsync(CreateView(V160));
        harness.Backend.ResetCounts();

        // Act
        var results = new[]
        {
            await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest"),
            await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1"),
            await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.6"),
            await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.6.0")
        };

        // Assert - publishing pre-resolves these, so a deploy does not hand the first request of each a
        // full version-list load.
        results.ShouldAllBe(r => r.IsSuccess);
        harness.Backend.LoadAllCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task RepeatedReads_ShouldNotReloadAsTimePasses()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");
        harness.Backend.ResetCounts();

        // Act - well past any resolution TTL, but with no publish in between.
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Options.ResolutionTtlSeconds - 1));
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Assert - resolution entries are not on a refresh treadmill; only a publish invalidates them.
        harness.Backend.LoadAllCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ConcurrentMisses_ShouldShareOneBackendLoad()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.CurrentGenerationAsync(); // bootstrap the token so only the resolution misses
        harness.Backend.ResetCounts();
        harness.Backend.Gate = new TaskCompletionSource();

        // Act - the fake cache completes synchronously, so all five reach the coalescing point while the
        // first is parked on the gate.
        var reads = Enumerable.Range(0, 5)
            .Select(_ => harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1"))
            .ToArray();

        harness.Backend.LoadAllCallCount.ShouldBe(1);
        harness.Backend.Gate.SetResult();
        var results = await Task.WhenAll(reads);

        // Assert
        results.ShouldAllBe(r => r.IsSuccess);
        results.ShouldAllBe(r => r.Value!.Version == V160);
        harness.Backend.LoadAllCallCount.ShouldBe(1);
    }

    // ────────────────────────────────────────────────────────────────────
    // Warming
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_ShouldWarmEvenWhenTheBackendCannotSeeTheNewVersionYet()
    {
        // Arrange - the publish has not become visible to a fresh query.
        var harness = new CacheSetTestHarness();
        harness.Publish();

        // Act
        await harness.Sut.SetAsync(CreateView(V160));
        harness.Backend.ResetCounts();

        // Assert - warming unions the published entity into the candidate set, so it does not cache an
        // answer computed from a list that is missing it.
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest");
        result.Value!.Version.ShouldBe(V160);
        harness.Backend.LoadAllCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_ShouldWarmOnlyTheAuthorableSpellings()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);

        // Act
        await harness.Sut.SetAsync(CreateView(V160));

        // Assert
        harness.ResolutionSpellingsInCache()
            .ShouldBe(["1", "1.6", "1.6.0", "latest"]);
    }

    // ────────────────────────────────────────────────────────────────────
    // Negative caching
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnmatchedRequest_ShouldBeCachedSoItDoesNotReloadEveryTime()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.CurrentGenerationAsync();
        harness.Backend.ResetCounts();

        // Act
        var first = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9");
        var loadsAfterFirst = harness.Backend.LoadAllCallCount;
        var second = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9");

        // Assert
        first.IsSuccess.ShouldBeFalse();
        second.IsSuccess.ShouldBeFalse();
        loadsAfterFirst.ShouldBe(1);
        harness.Backend.LoadAllCallCount.ShouldBe(1, "the negative answer should be served from cache");
    }

    [Fact]
    public async Task NegativeEntry_ShouldNotSurviveAPublishThatSatisfiesTheRequest()
    {
        // Arrange
        const string v900 = "9.0.0-pkg.1.0.0+core";

        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9")).IsSuccess.ShouldBeFalse();

        // Act
        harness.AddPublished(v900);
        await harness.Sut.SetAsync(CreateView(v900));

        // Assert - the generation bump clears negatives too, so the reference starts working at once
        // rather than after the negative TTL.
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9");
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe(v900);
    }

    [Fact]
    public async Task NegativeEntry_ShouldExpireOnItsOwnShortTtl()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9")).IsSuccess.ShouldBeFalse();

        // Act - a version appears without going through this cache set (for example published by another
        // pod whose bump this pod has already applied).
        harness.AddPublished("9.0.0-pkg.1.0.0+core");
        harness.Time.Advance(TimeSpan.FromSeconds(harness.Options.NegativeTtlSeconds + 1));

        // Assert
        (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9")).IsSuccess.ShouldBeTrue();
    }

    // ────────────────────────────────────────────────────────────────────
    // Build metadata
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FullVersionRequests_ShouldIgnoreBuildMetadata()
    {
        // Arrange
        const string withMetadata = "1.5.6-pkg.1.1.56+core";
        const string withoutMetadata = "1.5.6-pkg.1.1.56";

        var harness = new CacheSetTestHarness();
        harness.Publish(withMetadata);
        await harness.Sut.SetAsync(CreateView(withMetadata));
        harness.Backend.ResetCounts();

        // Act
        var qualified = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, withMetadata);
        var bare = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, withoutMetadata);

        // Assert - build metadata does not participate in comparison, so both name the same revision and
        // must share one cache entry.
        qualified.Value!.Version.ShouldBe(withMetadata);
        bare.Value!.Version.ShouldBe(withMetadata);
        harness.Backend.LoadCallCount.ShouldBe(0);
        harness.Cache.Keys.ShouldContain(harness.FullKey(withoutMetadata));
    }

    // ────────────────────────────────────────────────────────────────────
    // TestKey layout
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LatestSpellings_ShouldCollapseToASingleEntry()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.CurrentGenerationAsync();
        harness.Backend.ResetCounts();

        // Act
        foreach (var spelling in new[] { null, "", "latest", "LATEST", "  Latest  " })
        {
            (await harness.Sut.GetByVersionAsync(TestDomain, TestKey, spelling)).IsSuccess.ShouldBeTrue();
        }

        // Assert - equivalent requests must not produce independently staleable entries.
        harness.ResolutionSpellingsInCache().ShouldBe(["latest"]);
        harness.Backend.LoadAllCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ResolvingOneSpelling_ShouldNotWriteAnswersForOthers()
    {
        // Arrange - no publish, so nothing is warmed and only the read populates.
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);

        // Act
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Assert - guessing at resolutions nobody asked for is how a resolution cache goes wrong.
        harness.ResolutionSpellingsInCache().ShouldBe(["1"]);
    }

    [Fact]
    public async Task Read_ShouldAlsoWarmTheImmutableBody()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);

        // Act
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Assert - the body was loaded anyway, so a pinned read of that exact version can reuse it.
        harness.Cache.Keys.ShouldContain(harness.FullKey(V160));
    }

    [Fact]
    public async Task EveryCachedKey_ShouldCarryAnExpiry()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150, V160);
        await harness.Sut.SetAsync(CreateView(V160));
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1.5");
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "9");
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, V150);

        // Act
        var keysWithoutExpiry = harness.Cache.Keys.Where(harness.Cache.HasNoExpiry).ToList();

        // Assert - the original defect was an entry that never expired and was never invalidated. Even
        // with invalidation now complete, nothing should be able to outlive its bound.
        keysWithoutExpiry.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publish_ShouldApplyTheConfiguredTtlToEachKeyClass()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        var start = harness.Time.GetUtcNow();
        harness.Publish(V160);

        // Act
        await harness.Sut.SetAsync(CreateView(V160));
        var generation = await harness.CurrentGenerationAsync();

        // Assert
        harness.Cache.ExpiryOf(harness.FullKey(V160))
            .ShouldBe(start.AddSeconds(harness.Options.FullVersionTtlSeconds));
        harness.Cache.ExpiryOf(harness.ResolutionKey(generation, "latest"))
            .ShouldBe(start.AddSeconds(harness.Options.ResolutionTtlSeconds));
        harness.Cache.ExpiryOf(harness.GenerationKey())
            .ShouldBe(start.AddSeconds(harness.Options.GenerationTtlSeconds));
    }

    // ────────────────────────────────────────────────────────────────────
    // Legacy key purge
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_ShouldPurgeKeysFromThePreGenerationLayout()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);

        // Act
        await harness.Sut.SetAsync(CreateView(V160));

        // Assert - those keys had no expiration, so a rolling deployment would otherwise keep serving
        // them from pods running the old build.
        harness.Cache.Removes.ShouldContain(harness.LegacyLatestKey());
        harness.Cache.Removes.ShouldContain(harness.LegacyArtifactKey("1.6.0"));
        harness.Cache.Removes.ShouldContain(harness.LegacyArtifactKey("1.6"));
        harness.Cache.Removes.ShouldContain(harness.LegacyArtifactKey("1"));
    }

    [Fact]
    public async Task Publish_ShouldLeaveLegacyKeysAloneWhenPurgeIsDisabled()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Options.PurgeLegacyKeysOnPublish = false;
        harness.Publish(V160);

        // Act
        await harness.Sut.SetAsync(CreateView(V160));

        // Assert
        harness.Cache.Removes.ShouldNotContain(harness.LegacyLatestKey());
        harness.Cache.Removes.ShouldNotContain(harness.LegacyArtifactKey("1"));
    }

    // ────────────────────────────────────────────────────────────────────
    // Resilience
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_WhenTheCacheIsUnreadable_ShouldFallBackToTheBackend()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.Sut.SetAsync(CreateView(V160));

        // Act
        harness.Cache.FailReads = _ => true;
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Assert - an unreachable cache degrades to database reads, never to stale answers.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe(V160);
    }

    [Fact]
    public async Task GenerationRead_WhenTheCacheIsUnreadable_ShouldNotReplaceTheSharedToken()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        await harness.CurrentGenerationAsync();

        // Act
        harness.Cache.FailReads = key => key.EndsWith(":gen", StringComparison.Ordinal);
        harness.Cache.ClearLog();
        await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "1");

        // Assert - a failed read is not evidence the token is gone. Writing a replacement would
        // invalidate every other pod's resolutions, and would do so on every request for as long as
        // reads keep failing.
        harness.Cache.Writes.ShouldNotContain(harness.GenerationKey());
    }

    [Fact]
    public async Task Publish_WhenTheCacheIsUnwritable_ShouldStillSucceed()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V160);
        harness.Cache.FailWrites = _ => true;

        // Act
        var result = await harness.Sut.SetAsync(CreateView(V160));

        // Assert - publishing must not fail because a cache write did.
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Publish_WithNullEntity_ShouldFail()
    {
        // Arrange
        var harness = new CacheSetTestHarness();

        // Act
        var result = await harness.Sut.SetAsync(null!);

        // Assert
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Read_WhenNothingIsPublished_ShouldFail()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish();

        // Act
        var result = await harness.Sut.GetByVersionAsync(TestDomain, TestKey, "latest");

        // Assert
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task GetLatestByName_ShouldResolveTheHighestVersion()
    {
        // Arrange
        var harness = new CacheSetTestHarness();
        harness.Publish(V150, V160);

        // Act
        var result = await harness.Sut.GetLatestByNameAsync(TestDomain, TestKey);

        // Assert
        result.Value!.Version.ShouldBe(V160);
    }

    // ────────────────────────────────────────────────────────────────────
    // Options
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Options_ShouldDefaultToBoundedTtlsAndLegacyPurgeEnabled()
    {
        // Act
        var options = new ComponentCacheOptions();

        // Assert
        options.FullVersionTtlSeconds.ShouldBe(1800);
        options.GenerationTtlSeconds.ShouldBe(3600);
        options.ResolutionTtlSeconds.ShouldBe(3600);
        options.NegativeTtlSeconds.ShouldBe(30);
        options.GenerationMemoSeconds.ShouldBe(
            5,
            "the token read is unavoidable per resolution, so it is memoized briefly; the number is "
            + "the bounded staleness window a publish is allowed, and changing it is a policy decision");
        options.PurgeLegacyKeysOnPublish.ShouldBeTrue();
    }
}
