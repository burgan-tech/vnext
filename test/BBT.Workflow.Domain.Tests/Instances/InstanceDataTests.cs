using System;
using System.Linq;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for the <see cref="InstanceData"/> row itself (identity, hashing, ETag) and the
/// version arithmetic the write service applies to the head it reads under the row lock.
/// Rows are seeded through <see cref="InstanceDataSeeder"/> — the production write path is
/// <c>IInstanceDataWriteService</c>.
/// </summary>
public class InstanceDataTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void SeededRow_ShouldInitializeAllProperties()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var id = Guid.NewGuid();
        var data = JsonData.CreateFrom("{\"key\":\"value\"}");

        // Act
        var instanceData = instance.SeedData(id, data);

        // Assert
        Assert.Equal(id, instanceData.Id);
        Assert.Equal(instance.Id, instanceData.InstanceId);
        Assert.Equal("1.0.0", instanceData.Version);
        Assert.True(instanceData.IsLatest);
        Assert.NotNull(instanceData.ETag);
        Assert.NotNull(instanceData.DataHash);
        Assert.NotNull(instanceData.Data);
        Assert.NotEqual(default, instanceData.EnteredAt);
    }

    [Fact]
    public void SeedData_ShouldCreateNewVersion_WithIncrementedVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value1\"}"));

        var newId = Guid.NewGuid();

        // Act
        var newVersion = instance.SeedData(newId, JsonData.CreateFrom("{\"key\":\"value2\"}"), VersionStrategy.IncreasePatch);

        // Assert
        Assert.Equal(newId, newVersion.Id);
        Assert.Equal("1.0.1", newVersion.Version);
        Assert.True(newVersion.IsLatest);
        Assert.False(instance.DataList.First().IsLatest); // Old version should be marked as not latest
    }

    [Fact]
    public void SeedData_ShouldMergeJsonData()
    {
        // Arrange — full-merge model: every row carries the complete state.
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key1\":\"value1\"}"));

        // Act
        var newVersion = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key2\":\"value2\"}"), VersionStrategy.IncreasePatch);

        // Assert
        Assert.Contains("key1", newVersion.Data.Json);
        Assert.Contains("key2", newVersion.Data.Json);
    }

    [Theory]
    [InlineData("Major", "2.0.0")]
    [InlineData("Minor", "1.1.0")]
    [InlineData("Patch", "1.0.1")]
    public void IncrementVersion_ShouldIncrementFromTheHead(string strategyCode, string expectedVersion)
    {
        Assert.Equal(
            expectedVersion,
            InstanceData.IncrementVersion("1.0.0", VersionStrategy.FromCode(strategyCode)));
    }

    [Fact]
    public void HasSameData_ShouldReturnTrue_WhenDataIsIdentical()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\"}"));

        // Act & Assert
        Assert.True(instanceData.HasSameData(JsonData.CreateFrom("{\"key\":\"value\"}")));
    }

    [Fact]
    public void HasSameData_ShouldReturnFalse_WhenDataIsDifferent()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value1\"}"));

        // Act & Assert
        Assert.False(instanceData.HasSameData(JsonData.CreateFrom("{\"key\":\"value2\"}")));
    }

    [Fact]
    public void HasSameData_ShouldReturnTrue_WithDifferentJsonFormatting()
    {
        // Arrange - Same semantic content but different formatting
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\",\"number\":123}"));

        // Act — different order & spacing, same semantic content
        var result = instanceData.HasSameData(JsonData.CreateFrom("{ \"number\": 123, \"key\": \"value\" }"));

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void SeedData_ShouldMarkPreviousAsNotLatest()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var instanceData1 = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"));

        Assert.True(instanceData1.IsLatest);

        // Act
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"));

        // Assert
        Assert.False(instanceData1.IsLatest);
    }

    [Fact]
    public void InstanceData_ShouldHaveUniqueETag()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var instanceData1 = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\"}"));
        var instanceData2 = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value1\"}"));

        // Assert
        Assert.NotEqual(instanceData1.ETag, instanceData2.ETag);
    }

    [Fact]
    public void DataHash_ShouldBeConsistent_ForSameData()
    {
        // Arrange
        var instance1 = InstanceFactory.CreateDefault();
        var instance2 = InstanceFactory.CreateDefault();

        // Act
        var instanceData1 = instance1.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\"}"));
        var instanceData2 = instance2.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\"}"));

        // Assert
        Assert.Equal(instanceData1.DataHash, instanceData2.DataHash);
    }

    [Fact]
    public void DataHash_ShouldBeDifferent_ForDifferentData()
    {
        // Arrange — two independent instances so no merge interferes
        var instance1 = InstanceFactory.CreateDefault();
        var instance2 = InstanceFactory.CreateDefault();

        // Act
        var instanceData1 = instance1.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value1\"}"));
        var instanceData2 = instance2.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value2\"}"));

        // Assert
        Assert.NotEqual(instanceData1.DataHash, instanceData2.DataHash);
    }

    [Fact]
    public void DataHash_ShouldBeConsistent_WithNormalizedJson()
    {
        // Arrange - Same semantic content with different formatting
        var instance1 = InstanceFactory.CreateDefault();
        var instance2 = InstanceFactory.CreateDefault();

        // Act
        var instanceData1 = instance1.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"a\":1,\"b\":2}"));
        var instanceData2 = instance2.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{ \"b\": 2, \"a\": 1 }"));

        // Assert
        Assert.Equal(instanceData1.DataHash, instanceData2.DataHash);
    }

    [Fact]
    public void Attributes_ShouldReturnDynamicObject()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\",\"number\":42}"));

        // Act & Assert
        Assert.NotNull(instanceData.Attributes);
    }

    [Fact]
    public void SeededRow_ShouldSetEnteredAtToCurrentTime()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var before = DateTime.UtcNow.AddSeconds(-1);

        // Act
        var instanceData = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("{}"));
        var after = DateTime.UtcNow.AddSeconds(1);

        // Assert
        Assert.True(instanceData.EnteredAt >= before && instanceData.EnteredAt <= after);
    }

    [Fact]
    public void SeedDataWithVersion_ShouldUseProvidedVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var instanceData = instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), "2.5.3");

        // Assert
        Assert.Equal("2.5.3", instanceData.Version);
    }

    #region Extended Version Format Tests (MAJOR.MINOR.PATCH-pkg.PKG_VERSION+PKG_NAME)

    [Fact]
    public void SeedDataWithVersion_ShouldAcceptExtendedVersionFormat()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var extendedVersion = "1.0.0-pkg.1.17.0+account";

        // Act
        var instanceData = instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), extendedVersion);

        // Assert
        Assert.Equal(extendedVersion, instanceData.Version);
    }

    [Theory]
    [InlineData("1.0.0-pkg.1.17.0+account", "Major", "2.0.0-pkg.1.17.0+account")]
    [InlineData("1.0.0-pkg.1.17.0+account", "Minor", "1.1.0-pkg.1.17.0+account")]
    [InlineData("1.0.0-pkg.1.17.0+account", "Patch", "1.0.1-pkg.1.17.0+account")]
    [InlineData("2.5.3-pkg.10.20.30+myapp", "Major", "3.0.0-pkg.10.20.30+myapp")]
    [InlineData("2.5.3-pkg.10.20.30+myapp", "Minor", "2.6.0-pkg.10.20.30+myapp")]
    [InlineData("2.5.3-pkg.10.20.30+myapp", "Patch", "2.5.4-pkg.10.20.30+myapp")]
    public void IncrementVersion_ShouldPreservePackageVersionAndMetadata_WhenIncrementing(
        string originalVersion,
        string strategyCode,
        string expectedVersion)
    {
        Assert.Equal(
            expectedVersion,
            InstanceData.IncrementVersion(originalVersion, VersionStrategy.FromCode(strategyCode)));
    }

    [Fact]
    public void IncrementVersion_ShouldPreserveSuffix_WhenNoStrategyApplied()
    {
        // None keeps the head's version string untouched — the version line continues.
        var version = "1.0.0-pkg.1.17.0+account";

        Assert.Equal(version, InstanceData.IncrementVersion(version, VersionStrategy.None));
    }

    [Fact]
    public void SeedDataWithVersion_ShouldHandleMultipleExtendedVersions()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act - Add multiple versions with extended format
        var v1 = instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.2.0+account");
        var v2 = instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-pkg.1.2.1+account");
        var v3 = instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "2.0.0-pkg.1.3.0+account");

        // Assert
        Assert.Equal(3, instance.DataList.Count);
        Assert.Equal("1.0.0-pkg.1.2.0+account", v1.Version);
        Assert.Equal("1.0.0-pkg.1.2.1+account", v2.Version);
        Assert.Equal("2.0.0-pkg.1.3.0+account", v3.Version);
    }

    [Fact]
    public void LatestData_ShouldReturnHighestExtendedVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Add versions in non-sequential order
        instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-pkg.1.2.1+account");
        instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.2.0+account");
        instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "2.0.0-pkg.1.3.0+account");

        // Act
        var latest = instance.LatestData;

        // Assert - Should be the highest version (2.0.0-pkg.1.3.0+account)
        Assert.NotNull(latest);
        Assert.Equal("2.0.0-pkg.1.3.0+account", latest.Version);
    }

    #endregion

    #region Pre-Release Version Tests

    [Fact]
    public void SeedDataWithVersion_ShouldAcceptPreReleaseVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var preReleaseVersion = "1.0.0-alpha.1-pkg.1.17.0+account";

        // Act
        var instanceData = instance.SeedDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), preReleaseVersion);

        // Assert
        Assert.Equal(preReleaseVersion, instanceData.Version);
    }

    [Theory]
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "Major", "2.0.0-pkg.1.17.0+account")]
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "Minor", "1.1.0-pkg.1.17.0+account")]
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "Patch", "1.0.1-pkg.1.17.0+account")]
    [InlineData("1.0.0-beta-pkg.1.0.0+test", "Patch", "1.0.1-pkg.1.0.0+test")]
    [InlineData("2.5.3-rc.1-pkg.10.20.30+myapp", "Minor", "2.6.0-pkg.10.20.30+myapp")]
    public void IncrementVersion_ShouldDropPreRelease_WhenIncrementing(
        string originalVersion,
        string strategyCode,
        string expectedVersion)
    {
        // Pre-release should be dropped, pkg suffix preserved
        Assert.Equal(
            expectedVersion,
            InstanceData.IncrementVersion(originalVersion, VersionStrategy.FromCode(strategyCode)));
    }

    [Fact]
    public void IncrementVersion_ShouldHandleMultipleBuildMetadata()
    {
        Assert.Equal(
            "1.0.1-pkg.1.17.0+account+build.123",
            InstanceData.IncrementVersion("1.0.0-pkg.1.17.0+account+build.123", VersionStrategy.IncreasePatch));
    }

    #endregion
}
