using System;
using System.Collections.Generic;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Unit tests for the pure core of the explicit InstanceData write service
/// (<see cref="InstanceDataWriteService.AssignVersions"/>): sequential VersionNo assignment from
/// the authoritative head, stale-base semantic-version rebase, and multi-row chaining — all
/// without a database (the FOR UPDATE / SET LOCAL plumbing is exercised end-to-end against
/// PostgreSQL).
/// </summary>
public class InstanceDataWriteServiceTests
{
    private static readonly Guid InstanceId = Guid.NewGuid();

    [Fact]
    public void AssignVersions_FirstRowWithoutHead_StartsAtOne()
    {
        var row = CreateFirstRow("1.0.0");

        InstanceDataWriteService.AssignVersions(
            InstanceId, [row], head: null, NullLogger.Instance);

        row.VersionNo.ShouldBe(1);
        row.Version.ShouldBe("1.0.0");
    }

    [Fact]
    public void AssignVersions_FreshBase_AssignsHeadPlusOneWithoutRebase()
    {
        // In-memory base WAS the head — no concurrent commit; version untouched.
        var row = CreateNewRow("1.0.5", VersionStrategy.IncreasePatch); // computed 1.0.6

        InstanceDataWriteService.AssignVersions(
            InstanceId, [row], Head(versionNo: 6, version: "1.0.5", historySequence: 0), NullLogger.Instance);

        row.VersionNo.ShouldBe(7);
        row.Version.ShouldBe("1.0.6");
    }

    [Fact]
    public void AssignVersions_StaleBase_RebasesOntoDbHead()
    {
        // Row computed off stale 1.0.5, but a concurrent writer already committed 1.0.6.
        var row = CreateNewRow("1.0.5", VersionStrategy.IncreasePatch); // computed 1.0.6 (duplicate!)

        InstanceDataWriteService.AssignVersions(
            InstanceId, [row], Head(versionNo: 7, version: "1.0.6", historySequence: 0), NullLogger.Instance);

        row.VersionNo.ShouldBe(8);
        row.Version.ShouldBe("1.0.7"); // rebased — no duplicate SemVer
    }

    [Fact]
    public void AssignVersions_NoneStrategyOnStaleBase_ContinuesHeadHistoryLine()
    {
        var row = CreateNewRow("1.0.5", VersionStrategy.None); // computed 1.0.5 again

        InstanceDataWriteService.AssignVersions(
            InstanceId, [row], Head(versionNo: 9, version: "1.0.6", historySequence: 2), NullLogger.Instance);

        row.VersionNo.ShouldBe(10);
        row.Version.ShouldBe("1.0.6");
        row.HistorySequence.ShouldBe(3);
    }

    [Fact]
    public void AssignVersions_MultipleRows_ChainSequentially()
    {
        // Two rows added in one SaveChanges (e.g. parallel-branch merge): the first becomes
        // the effective head for the second.
        var first = CreateNewRow("1.0.5", VersionStrategy.IncreasePatch);              // 1.0.6
        var second = first.NewVersion(Guid.NewGuid(), new JsonData("{\"c\":3}"),
            VersionStrategy.IncreasePatch, 0);                                          // 1.0.7

        var rows = new List<InstanceData> { second, first }; // deliberately out of order

        InstanceDataWriteService.AssignVersions(
            InstanceId, rows, Head(versionNo: 6, version: "1.0.5", historySequence: 0), NullLogger.Instance);

        first.VersionNo.ShouldBe(7);
        second.VersionNo.ShouldBe(8);
        first.Version.ShouldBe("1.0.6");
        second.Version.ShouldBe("1.0.7");
    }

    [Fact]
    public void AssignVersions_ExplicitVersionAppend_KeepsAuthoredVersion()
    {
        // AddDataWithVersion-style rows carry no strategy — authored version preserved even
        // when it sits below the head; VersionNo alone separates them.
        var row = new InstanceData(
            Guid.NewGuid(), InstanceId, "0.9.0", new JsonData("{}"), false, 1);

        InstanceDataWriteService.AssignVersions(
            InstanceId, [row], Head(versionNo: 12, version: "2.0.0", historySequence: 0), NullLogger.Instance);

        row.VersionNo.ShouldBe(13);
        row.Version.ShouldBe("0.9.0");
    }

    private static InstanceDataHeadRow Head(long versionNo, string version, int historySequence)
        => new() { VersionNo = versionNo, Version = version, HistorySequence = historySequence };

    private static InstanceData CreateFirstRow(string version)
        => new(Guid.NewGuid(), InstanceId, version, new JsonData("{\"a\":1}"), true);

    private static InstanceData CreateNewRow(string staleBaseVersion, VersionStrategy strategy)
    {
        var staleBase = new InstanceData(
            Guid.NewGuid(), InstanceId, staleBaseVersion, new JsonData("{\"a\":1}"), true);

        return staleBase.NewVersion(Guid.NewGuid(), new JsonData("{\"b\":2}"), strategy, 0);
    }
}
