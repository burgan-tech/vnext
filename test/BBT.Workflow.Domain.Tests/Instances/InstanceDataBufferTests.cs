using System;
using BBT.Aether;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The in-memory data line of a <c>history: none</c> instance (vnext#1006): the pending row overlays
/// the aggregate's latest without entering the EF navigation, snapshots share it, and the flushed
/// version folds the accepted strategies over the persisted head.
/// </summary>
public class InstanceDataBufferTests
{
    [Fact]
    public void PendingRow_IsLatest_ButNeverInDataList()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();

        var row = Accept(instance, """{"a":1}""", "1.0.0");

        instance.LatestData.ShouldBeSameAs(row);
        instance.FindData(null).ShouldBeSameAs(row);
        instance.FindData("1.0.0").ShouldBeSameAs(row);
        instance.DataList.ShouldBeEmpty();
    }

    [Fact]
    public void AcceptPersistedData_IgnoresThePendingRow()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        var row = Accept(instance, """{"a":1}""", "1.0.0");

        instance.AcceptPersistedData(row.CreateSnapshot());

        instance.DataList.ShouldBeEmpty();
    }

    [Fact]
    public void Snapshot_SharesTheBuffer()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        var snapshot = instance.CreateSnapshot();

        var row = Accept(snapshot, """{"a":1}""", "1.0.0");

        snapshot.DataBuffer.ShouldBeSameAs(instance.DataBuffer);
        instance.LatestData.ShouldBeSameAs(row);
    }

    [Fact]
    public void EnableDataBuffering_IsIdempotent_AndStartsFromTheLatestRow()
    {
        var instance = InstanceFactory.CreateDefault();
        var seeded = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"x":1}"""));

        instance.EnableDataBuffering();
        var buffer = instance.DataBuffer;
        instance.EnableDataBuffering();

        instance.DataBuffer.ShouldBeSameAs(buffer);
        buffer!.BaseRow.ShouldBeSameAs(seeded);
        buffer.HasPendingChanges.ShouldBeFalse();
        instance.LatestData.ShouldBeSameAs(seeded);
    }

    [Fact]
    public void ResolveVersion_OnEmptyHead_StartsAtDefault_AndSkipsTheFirstStrategy()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        Accept(instance, """{"a":1}""", WorkflowConstants.DefaultVersion, VersionStrategy.IncreaseMajor);
        Accept(instance, """{"a":2}""", "x", VersionStrategy.IncreaseMinor);

        instance.DataBuffer!.ResolveVersion(null)
            .ShouldBe(InstanceData.IncrementVersion(WorkflowConstants.DefaultVersion, VersionStrategy.IncreaseMinor));
    }

    [Fact]
    public void ResolveVersion_OnPersistedHead_FoldsEveryStrategy()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        Accept(instance, """{"a":1}""", "x", VersionStrategy.IncreaseMinor);
        Accept(instance, """{"a":2}""", "y", VersionStrategy.IncreaseMinor);

        instance.DataBuffer!.ResolveVersion("1.0.0").ShouldBe("1.2.0");
    }

    [Fact]
    public void MarkFlushed_RebasesAndClears()
    {
        var instance = InstanceFactory.CreateDefault();
        instance.EnableDataBuffering();
        Accept(instance, """{"a":1}""", "1.0.0");
        var persisted = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"a":1}"""));

        instance.DataBuffer!.MarkFlushed(persisted);

        instance.DataBuffer.HasPendingChanges.ShouldBeFalse();
        instance.DataBuffer.PendingRow.ShouldBeNull();
        instance.DataBuffer.BaseRow.ShouldBeSameAs(persisted);
        instance.LatestData.ShouldBeSameAs(persisted);
    }

    [Fact]
    public void RequiresHistoryNoneChild_OnlyForSubFlowStampedNone()
    {
        Stamp("none", "S").RequiresHistoryNoneChild().ShouldBeTrue();
        Stamp("none", "P").RequiresHistoryNoneChild().ShouldBeFalse();
        Stamp(null, "S").RequiresHistoryNoneChild().ShouldBeFalse();
        Stamp("full", "S").RequiresHistoryNoneChild().ShouldBeFalse();

        // Remote starts carry the values as JsonElement.
        var remote = new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.History] = System.Text.Json.JsonDocument.Parse("\"none\"").RootElement,
            [DomainConsts.MetaDataKeys.FlowType] = System.Text.Json.JsonDocument.Parse("\"S\"").RootElement
        };
        remote.RequiresHistoryNoneChild().ShouldBeTrue();
    }

    private static ExtraPropertyDictionary Stamp(string? history, string flowType)
    {
        var md = new ExtraPropertyDictionary { [DomainConsts.MetaDataKeys.FlowType] = flowType };
        if (history is not null)
            md[DomainConsts.MetaDataKeys.History] = history;
        return md;
    }

    private static InstanceData Accept(Instance instance, string json, string version, VersionStrategy? strategy = null)
    {
        var content = JsonData.CreateFrom(json);
        return instance.DataBuffer!.Accept(
            instance.Id, content, InstanceData.ComputeDataHash(content), version, content, strategy);
    }
}
