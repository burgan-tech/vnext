using System;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the in-memory latest invariant and the latest-only loading guards.
/// <c>IsLatest</c> always points to the highest version across the entire history (comparer
/// semantics): parallel artifact lines (1.0.x vs 2.0.x) evolve independently and an append to an
/// older line must not steal the global latest flag. Latest-only loaded aggregates fail fast on
/// history-dependent reads instead of silently answering wrong.
/// </summary>
public class InstanceDataLatestInvariantTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void SeedDataWithVersion_LowerVersion_ShouldNotStealLatestFlag()
    {
        // Arrange — history head is 2.0.0
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"two\"}"), VersionStrategy.IncreaseMajor); // 2.0.0

        // Act — data lands on the 1.0.x line
        var lineAppend = instance.SeedDataWithVersion(
            Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"line\"}"), "1.0.5");

        // Assert — the line grows but the global latest stays on 2.0.0
        Assert.Equal("1.0.5", lineAppend.Version);
        Assert.False(lineAppend.IsLatest);
        Assert.Equal("2.0.0", instance.LatestData!.Version);
        Assert.True(instance.LatestData.IsLatest);
    }

    [Fact]
    public void SeedDataWithVersion_HigherVersion_ShouldTakeLatestFlag()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        var previousLatest = instance.LatestData!;

        // Act
        var newHead = instance.SeedDataWithVersion(
            Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"two\"}"), "1.1.0");

        // Assert
        Assert.True(newHead.IsLatest);
        Assert.False(previousLatest.IsLatest);
        Assert.Equal("1.1.0", instance.LatestData!.Version);
    }

    [Fact]
    public void GetVersionHistory_OnPartiallyLoadedAggregate_ShouldThrow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}"));
        instance.MarkDataPartiallyLoaded();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => instance.GetVersionHistory("1.0.0"));
    }

    [Fact]
    public void FindData_LatestRequests_OnPartiallyLoadedAggregate_ShouldSucceed()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
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
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":\"one\"}")); // 1.0.0
        instance.MarkDataPartiallyLoaded();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => instance.FindData("0.9.0"));
    }
}
