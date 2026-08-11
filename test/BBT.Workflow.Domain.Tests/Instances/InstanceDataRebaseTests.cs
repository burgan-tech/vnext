using System;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for <see cref="InstanceData.RebaseVersion"/> — the write funnel re-applies a new
/// row's version strategy onto the real database head when the in-memory base was stale
/// (a concurrent writer committed in between).
/// </summary>
public class InstanceDataRebaseTests
{
    [Theory]
    [InlineData("Patch", "1.0.6", "1.0.7")]
    [InlineData("Minor", "1.0.6", "1.1.0")]
    [InlineData("Major", "1.0.6", "2.0.0")]
    public void RebaseVersion_BumpStrategies_BumpFromDbHead(
        string strategyCode, string dbHeadVersion, string expected)
    {
        // Stale base 1.0.5 → in-memory computed off it; real head moved to 1.0.6 meanwhile.
        var row = CreateNewRow("1.0.5", VersionStrategy.FromCode(strategyCode));

        row.RebaseVersion(dbHeadVersion, dbHeadHistorySequence: 3);

        row.Version.ShouldBe(expected);
        row.HistorySequence.ShouldBe(0); // new version line starts a fresh history sequence
    }

    [Fact]
    public void RebaseVersion_NoneStrategy_ContinuesDbHeadLineWithNextHistorySequence()
    {
        var row = CreateNewRow("1.0.5", VersionStrategy.None);

        row.RebaseVersion("1.0.6", dbHeadHistorySequence: 3);

        row.Version.ShouldBe("1.0.6");        // None keeps the head string
        row.HistorySequence.ShouldBe(4);      // continues the head's history line
    }

    [Fact]
    public void RebaseVersion_PreservesPkgSuffixAndMetadataFromDbHead()
    {
        var row = CreateNewRow("1.0.5-pkg.1.17.0+account", VersionStrategy.IncreasePatch);

        row.RebaseVersion("1.0.6-pkg.1.17.0+account", dbHeadHistorySequence: 0);

        row.Version.ShouldBe("1.0.7-pkg.1.17.0+account");
    }

    [Fact]
    public void RebaseVersion_UnknownStrategy_IsNoOp()
    {
        // First row / explicit-version appends carry no strategy — the funnel must not touch
        // their authored version; VersionNo alone separates them.
        var instanceId = Guid.NewGuid();
        var row = new InstanceData(Guid.NewGuid(), instanceId, "3.2.1", new JsonData("{}"), true, 5);

        row.RebaseVersion("9.9.9", dbHeadHistorySequence: 7);

        row.Version.ShouldBe("3.2.1");
        row.HistorySequence.ShouldBe(5);
    }

    /// <summary>
    /// Builds a NEW row the way <see cref="Instance.AddData"/> does: from a stale base via
    /// <c>NewVersion</c>, so <c>AppliedVersionStrategy</c> is stamped.
    /// </summary>
    private static InstanceData CreateNewRow(string staleBaseVersion, VersionStrategy strategy)
    {
        var instanceId = Guid.NewGuid();
        var staleBase = new InstanceData(
            Guid.NewGuid(), instanceId, staleBaseVersion, new JsonData("{\"a\":1}"), true);

        return staleBase.NewVersion(Guid.NewGuid(), new JsonData("{\"b\":2}"), strategy, 0);
    }
}
