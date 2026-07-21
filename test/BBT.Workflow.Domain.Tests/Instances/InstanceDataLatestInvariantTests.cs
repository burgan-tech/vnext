using System;
using System.Linq;
using BBT.Aether.DependencyInjection;
using BBT.Workflow.DefinitionContext;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the accepted latest invariant: <c>IsLatest</c> always points to the highest
/// version across the entire history (comparer semantics). Parallel artifact lines (1.0.x vs
/// 2.0.x) evolve independently — appending to an older line must not steal the global latest
/// flag — and latest-only loaded aggregates fail fast on history-dependent operations.
/// </summary>
public class InstanceDataLatestInvariantTests : DomainTestBase<DomainEntryPoint>
{
    public InstanceDataLatestInvariantTests()
    {
        // The [SchemaValidation] aspect woven into Instance.AddData/AddDataWithVersion resolves
        // its services from the ambient (AsyncLocal) provider. Give this test class a minimal,
        // self-sufficient one — no workflow in context, so validation is skipped — instead of
        // depending on fixture ordering across parallel test collections.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowContext>(new NullWorkflowContext());
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    private sealed class NullWorkflowContext : IWorkflowContext
    {
        public Definitions.Workflow? Workflow => null;
        public bool HasWorkflow => false;
        public void SetWorkflow(Definitions.Workflow workflow)
        {
        }
    }

    [Fact]
    public void AddDataWithVersion_LowerVersion_ShouldNotStealLatestFlag()
    {
        // Arrange — history head is 2.0.0
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"two\"}"), VersionStrategy.IncreaseMajor); // 2.0.0

        // Act — user appends data to the 1.0.x line
        var lineAppend = instance.AddDataWithVersion(
            Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"line\"}"), "1.0.5");

        // Assert — the line grows but the global latest stays on 2.0.0
        Assert.Equal("1.0.5", lineAppend.Version);
        Assert.False(lineAppend.IsLatest);
        Assert.Equal("2.0.0", instance.LatestData!.Version);
        Assert.True(instance.LatestData.IsLatest);
    }

    [Fact]
    public void AddDataWithVersion_HigherVersion_ShouldTakeLatestFlag()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        var previousLatest = instance.LatestData!;

        // Act
        var newHead = instance.AddDataWithVersion(
            Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"two\"}"), "1.1.0");

        // Assert
        Assert.True(newHead.IsLatest);
        Assert.False(previousLatest.IsLatest);
        Assert.Equal("1.1.0", instance.LatestData!.Version);
    }

    [Fact]
    public void AddDataWithVersion_SameDataAsGlobalLatest_OnOlderLine_ShouldStillAppend()
    {
        // Arrange — regression for the old dedup leak: dedup compared against the GLOBAL
        // latest, silently skipping a line append whose payload happened to match it
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        var globalLatestPayload = "{\"v\":\"two\"}";
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom(globalLatestPayload), VersionStrategy.IncreaseMajor); // 2.0.0
        var countBefore = instance.DataList.Count;

        // Act — append the same payload to the 1.0.x line
        var lineAppend = instance.AddDataWithVersion(
            Guid.NewGuid(), JsonData.CreateFrom(globalLatestPayload), "1.0.1");

        // Assert — appended to the line instead of being deduped against 2.0.0
        Assert.Equal("1.0.1", lineAppend.Version);
        Assert.Equal(countBefore + 1, instance.DataList.Count);
        Assert.Equal("2.0.0", instance.LatestData!.Version);
    }

    [Fact]
    public void AddDataWithVersion_SameDataOnSameVersionLine_ShouldDedup()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var payload = "{\"v\":\"one\"}";
        var original = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom(payload), "1.0.0");
        var countBefore = instance.DataList.Count;

        // Act — re-append identical data to the same version line
        var result = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom(payload), "1.0.0");

        // Assert — the existing line head is returned, no new row
        Assert.Equal(original.Id, result.Id);
        Assert.Equal(countBefore, instance.DataList.Count);
    }

    [Fact]
    public void AddDataWithVersion_OlderLine_OnPartiallyLoadedAggregate_ShouldThrow()
    {
        // Arrange — latest-only loaded aggregates cannot compute line-head dedup/sequence
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"two\"}"), VersionStrategy.IncreaseMajor); // 2.0.0
        instance.MarkDataPartiallyLoaded();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"x\"}"), "1.0.5"));
    }

    [Fact]
    public void GetVersionHistory_OnPartiallyLoadedAggregate_ShouldThrow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}"));
        instance.MarkDataPartiallyLoaded();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => instance.GetVersionHistory("1.0.0"));
    }

    [Fact]
    public void FindData_LatestRequests_OnPartiallyLoadedAggregate_ShouldSucceed()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        instance.MarkDataPartiallyLoaded();

        // Act & Assert — "latest" style requests are always answerable
        Assert.NotNull(instance.FindData(null));
        Assert.NotNull(instance.FindData("latest"));
        Assert.NotNull(instance.FindData("1.0.0")); // resolves to the loaded (highest) row
    }

    [Fact]
    public void FindData_UnresolvedExplicitVersion_OnPartiallyLoadedAggregate_ShouldThrow()
    {
        // Arrange — a miss is ambiguous under latest-only load ("missing" vs "not loaded")
        var instance = InstanceFactory.CreateDefault();
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        instance.MarkDataPartiallyLoaded();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => instance.FindData("0.9.0"));
    }

    [Fact]
    public void AddDataWithVersion_LineAppend_ShouldIncrementHistorySequenceWithinLine()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"a\"}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"b\"}"), "2.0.0");

        // Act — two distinct appends to the same 1.0.0 line
        var first = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"c\"}"), "1.0.0");
        var second = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"d\"}"), "1.0.0");

        // Assert — sequence advances within the line; latest stays on 2.0.0
        Assert.True(second.HistorySequence > first.HistorySequence);
        Assert.Equal("2.0.0", instance.LatestData!.Version);
        Assert.Equal(
            new[] { "1.0.0" },
            instance.GetVersionHistory("1.0.0").Select(d => d.Version).Distinct().ToArray());
    }
}
