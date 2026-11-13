using System;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Pins how a floating request (<c>1.0.0</c>, <c>1.0</c>, <c>1</c>, <c>latest</c>) is resolved on a cache
/// miss and on publish: the decision is made from the version strings, only the winner's body is read (and
/// through the full-version entry when one exists), and a winner that cannot be read fails the request
/// rather than letting an older version answer for it.
/// </summary>
public sealed class CacheSetLeanResolutionTests
{
    private const string Domain = CacheSetTestHarness.TestDomain;
    private const string Key = CacheSetTestHarness.TestKey;

    [Fact]
    public async Task ArtifactRequest_ResolvesTheHighestPackageFromVersionNamesAndLoadsOneBody()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.9+core", "1.0.0-pkg.1.0.123+core", "1.0.0-pkg.1.0.99+core", "1.1.0-pkg.1.0.1+core");

        var result = await harness.Sut.GetByVersionAsync(Domain, Key, "1.0.0");

        result.Value!.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
        harness.Backend.VersionListCallCount.ShouldBe(1);
        harness.Backend.LoadCallCount.ShouldBe(1);
        harness.Backend.LoadAllCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ResolutionMiss_ReusesTheWinnersFullEntryInsteadOfReadingTheBody()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.99+core", "1.0.0-pkg.1.0.123+core");
        await harness.Sut.GetByVersionAsync(Domain, Key, "1.0.0-pkg.1.0.123+core");
        harness.Backend.ResetCounts();

        var result = await harness.Sut.GetByVersionAsync(Domain, Key, "1.0");

        result.Value!.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
        harness.Backend.VersionListCallCount.ShouldBe(1);
        harness.Backend.LoadCallCount.ShouldBe(0, "the winner's body was already cached under its full key");
    }

    [Fact]
    public async Task ResolutionMiss_WritesTheWinnerUnderItsFullKeyToo()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.123+core");

        await harness.Sut.GetByVersionAsync(Domain, Key, "1");

        harness.Cache.Keys.ShouldContain(harness.FullKey("1.0.0-pkg.1.0.123+core"));
    }

    [Fact]
    public async Task UnreadableWinner_FailsTheRequestAndCachesNoAnswer()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.99+core", "1.0.0-pkg.1.0.123+core");
        harness.Backend.UnreadableVersions.Add("1.0.0-pkg.1.0.123+core");

        await Should.ThrowAsync<InvalidOperationException>(
            () => harness.Sut.GetByVersionAsync(Domain, Key, "1.0.0"));

        harness.ResolutionSpellingsInCache().ShouldBeEmpty("an older version must not be cached as the answer");

        harness.Backend.UnreadableVersions.Clear();
        var retried = await harness.Sut.GetByVersionAsync(Domain, Key, "1.0.0");
        retried.Value!.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
    }

    [Fact]
    public async Task Publish_NewestVersion_WarmsEverySpellingWithoutReadingAnyBody()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.99+core");
        var published = CacheSetTestHarness.CreateView("1.0.0-pkg.1.0.123+core");
        harness.AddPublished(published.Version);

        await harness.Sut.SetAsync(published);

        harness.Backend.VersionListCallCount.ShouldBe(1);
        harness.Backend.LoadCallCount.ShouldBe(0);
        harness.Backend.LoadAllCallCount.ShouldBe(0);
        harness.ResolutionSpellingsInCache().ShouldBe(["1", "1.0", "1.0.0", "latest"]);
    }

    [Fact]
    public async Task Publish_OlderPatch_WarmsTheNewerWinnerFromOneBodyRead()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.123+core");
        var olderPatch = CacheSetTestHarness.CreateView("1.0.0-pkg.1.0.99+core");
        harness.AddPublished(olderPatch.Version);

        await harness.Sut.SetAsync(olderPatch);
        harness.Backend.LoadCallCount.ShouldBe(1, "every spelling shares the one winner");
        harness.Backend.ResetCounts();

        var resolved = await harness.Sut.GetByVersionAsync(Domain, Key, "1.0.0");

        resolved.Value!.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
        harness.Backend.VersionListCallCount.ShouldBe(0);
        harness.Backend.LoadCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Publish_WhenTheWinnerCannotBeRead_StillPublishesAndLeavesThatSpellingToResolveOnDemand()
    {
        var harness = new CacheSetTestHarness();
        harness.Publish("1.0.0-pkg.1.0.123+core");
        harness.Backend.UnreadableVersions.Add("1.0.0-pkg.1.0.123+core");
        var olderPatch = CacheSetTestHarness.CreateView("1.0.0-pkg.1.0.99+core");
        harness.AddPublished(olderPatch.Version);

        var published = await harness.Sut.SetAsync(olderPatch);

        published.IsSuccess.ShouldBeTrue();
        harness.ResolutionSpellingsInCache().ShouldBeEmpty();
        harness.Backend.LoadCallCount.ShouldBe(1, "the spellings share the winner, which is tried once");
    }
}
