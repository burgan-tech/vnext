using System;
using System.Linq;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Data;

/// <summary>
/// Unit tests for the in-memory halves of the immediate-persist write model: the aggregate
/// refresh (<see cref="Instance.AcceptPersistedData"/> — Id-idempotent, keeps the single-latest
/// invariant) and the version computation (<see cref="InstanceData.IncrementVersion"/>) the
/// service applies to the head it reads under the row lock. The FOR UPDATE / SET LOCAL plumbing
/// is exercised end-to-end against PostgreSQL.
/// </summary>
public class InstanceDataWriteServiceTests
{
    private static readonly Guid InstanceId = Guid.NewGuid();

    [Fact]
    public void AcceptPersistedData_NewLatestRow_DemotesOldLatestAndAdds()
    {
        var instance = CreateInstanceWithLatest("1.0.0", out var oldLatest);
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.1", new JsonData("{\"b\":2}"), true)
        {
            VersionNo = 2
        };

        instance.AcceptPersistedData(row);

        instance.DataList.Count.ShouldBe(2);
        oldLatest.IsLatest.ShouldBeFalse();
        instance.LatestData!.Id.ShouldBe(row.Id);
        instance.DataList.Count(d => d.IsLatest).ShouldBe(1);
    }

    [Fact]
    public void AcceptPersistedData_SameIdTwice_IsIdempotent()
    {
        // EF relationship fixup may have already attached the row when the service shares the
        // aggregate's DbContext — the explicit accept must not duplicate it.
        var instance = CreateInstanceWithLatest("1.0.0", out _);
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.1", new JsonData("{\"b\":2}"), true)
        {
            VersionNo = 2
        };

        instance.AcceptPersistedData(row);
        instance.AcceptPersistedData(row);

        instance.DataList.Count.ShouldBe(2);
        instance.DataList.Count(d => d.IsLatest).ShouldBe(1);
    }

    [Fact]
    public void AcceptPersistedData_OlderLineRow_DoesNotStealLatest()
    {
        // The explicit publish path can append below the head; the latest flag stays put.
        var instance = CreateInstanceWithLatest("2.0.0", out var head);
        var olderLine = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.5", new JsonData("{\"o\":1}"), false)
        {
            VersionNo = 2
        };

        instance.AcceptPersistedData(olderLine);

        head.IsLatest.ShouldBeTrue();
        instance.LatestData!.Id.ShouldBe(head.Id);
    }

    [Theory]
    [InlineData("Patch", "1.2.1", "1.2.2")]
    [InlineData("Minor", "1.2.1", "1.3.0")]
    [InlineData("Major", "1.2.1", "2.0.0")]
    [InlineData("None", "1.2.1", "1.2.1")]
    public void IncrementVersion_AppliesStrategyToTheHead(string strategy, string head, string expected)
    {
        InstanceData.IncrementVersion(head, VersionStrategy.FromCode(strategy)).ShouldBe(expected);
    }

    [Fact]
    public void IncrementVersion_PreservesPkgSuffixAndMetadata()
    {
        InstanceData.IncrementVersion("1.0.5-pkg.1.17.0+account", VersionStrategy.IncreasePatch)
            .ShouldBe("1.0.6-pkg.1.17.0+account");
    }

    [Fact]
    public void PlanAppend_NoHead_StartsTheChainAtDefaultVersion()
    {
        var plan = InstanceDataWriteService.PlanAppend(null, new JsonData("{\"a\":1}"), VersionStrategy.IncreasePatch);

        plan.IsDuplicate.ShouldBeFalse();
        plan.Version.ShouldBe("1.0.0");
        plan.VersionNo.ShouldBe(1L);
    }

    [Fact]
    public void PlanAppend_DeltaProducingNoChange_IsDuplicate()
    {
        // Regression pin: the dedup compares the hash of the MERGED result against the head's
        // hash. A delta-only duplicate (idempotent callback re-stamping an already-set key)
        // never matches the head raw — merged it does, and no new version may be written.
        var head = CreateHeadRow("{\"a\":1,\"rr_doc1\":true}", "1.2.3", versionNo: 7);

        var plan = InstanceDataWriteService.PlanAppend(head, new JsonData("{\"rr_doc1\":true}"), VersionStrategy.IncreaseMinor);

        plan.IsDuplicate.ShouldBeTrue();
    }

    [Fact]
    public void PlanAppend_DeltaProducingChange_MergesAndComputesIdentityFromTheHead()
    {
        var head = CreateHeadRow("{\"a\":1}", "1.2.3", versionNo: 7);

        var plan = InstanceDataWriteService.PlanAppend(head, new JsonData("{\"b\":2}"), VersionStrategy.IncreasePatch);

        plan.IsDuplicate.ShouldBeFalse();
        // Full-merge model: the new row carries the complete state, not the delta.
        plan.Content.Json.ShouldContain("\"a\"");
        plan.Content.Json.ShouldContain("\"b\"");
        plan.Version.ShouldBe("1.2.4");
        plan.VersionNo.ShouldBe(8L);
    }

    [Fact]
    public void PlanAppend_NoStrategy_ContinuesTheHeadVersionLine()
    {
        var head = CreateHeadRow("{\"a\":1}", "1.2.3", versionNo: 7);

        var plan = InstanceDataWriteService.PlanAppend(head, new JsonData("{\"a\":2}"), versionStrategy: null);

        plan.IsDuplicate.ShouldBeFalse();
        plan.Version.ShouldBe("1.2.3");
        plan.VersionNo.ShouldBe(8L);
    }

    private static InstanceDataHeadRow CreateHeadRow(string json, string version, long versionNo)
    {
        var data = new JsonData(json);
        return new InstanceDataHeadRow
        {
            VersionNo = versionNo,
            Version = version,
            Data = data.Json,
            DataHash = InstanceData.ComputeDataHash(data)
        };
    }

    private static Instance CreateInstanceWithLatest(string version, out InstanceData latest)
    {
        var instance = Instance.Create(InstanceId, "test-flow", "1.0.0");
        latest = new InstanceData(Guid.NewGuid(), instance.Id, version, new JsonData("{\"a\":1}"), true)
        {
            VersionNo = 1
        };
        instance.AcceptPersistedData(latest);
        return instance;
    }
}
