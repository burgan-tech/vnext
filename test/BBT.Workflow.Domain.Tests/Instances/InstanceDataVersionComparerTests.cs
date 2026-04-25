using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Unit tests for InstanceDataVersionComparer
/// </summary>
public class InstanceDataVersionComparerTests : DomainTestBase<DomainEntryPoint>
{
    private readonly InstanceDataVersionComparer _comparer;

    public InstanceDataVersionComparerTests()
    {
        _comparer = InstanceDataVersionComparer.Instance;
    }

    [Fact]
    public void Instance_ShouldReturnSingletonInstance()
    {
        // Arrange & Act
        var instance1 = InstanceDataVersionComparer.Instance;
        var instance2 = InstanceDataVersionComparer.Instance;

        // Assert
        Assert.Same(instance1, instance2);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)] // 1.0.0 < 1.0.1
    [InlineData("1.0.1", "1.0.0", 1)]  // 1.0.1 > 1.0.0
    [InlineData("1.0.0", "1.0.0", 0)]  // 1.0.0 == 1.0.0
    [InlineData("1.0.0", "1.1.0", -1)] // 1.0.0 < 1.1.0
    [InlineData("1.1.0", "1.0.0", 1)]  // 1.1.0 > 1.0.0
    [InlineData("1.0.0", "2.0.0", -1)] // 1.0.0 < 2.0.0
    [InlineData("2.0.0", "1.0.0", 1)]  // 2.0.0 > 1.0.0
    [InlineData("1.2.3", "1.2.4", -1)] // 1.2.3 < 1.2.4
    [InlineData("1.2.4", "1.2.3", 1)]  // 1.2.4 > 1.2.3
    public void Compare_ShouldReturnCorrectResult_ForSemanticVersions(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {version1} < {version2}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {version1} > {version2}");
        else
            Assert.Equal(0, result);
    }

    [Fact]
    public void Compare_ShouldReturnZero_WhenBothAreNull()
    {
        // Arrange
        InstanceData? data1 = null;
        InstanceData? data2 = null;

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        Assert.Equal(0, result);
    }

    [Fact]
    public void Compare_ShouldReturnNegative_WhenFirstIsNull()
    {
        // Arrange
        InstanceData? data1 = null;
        var data2 = CreateInstanceData("1.0.0");

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        Assert.True(result < 0);
    }

    [Fact]
    public void Compare_ShouldReturnPositive_WhenSecondIsNull()
    {
        // Arrange
        var data1 = CreateInstanceData("1.0.0");
        InstanceData? data2 = null;

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        Assert.True(result > 0);
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(null, "1.0.0", -1)]
    [InlineData("1.0.0", null, 1)]
    public void Compare_ShouldHandleNullVersions(string? version1, string? version2, int expected)
    {
        // Arrange
        // InstanceData boş string version kabul etmez, sadece null test ediyoruz
        var data1 = version1 != null ? CreateInstanceData(version1) : null;
        var data2 = version2 != null ? CreateInstanceData(version2) : null;

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0);
        else if (expected > 0)
            Assert.True(result > 0);
        else
            Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("invalid1", "invalid2", -1)] // String comparison fallback: "invalid1" < "invalid2"
    [InlineData("abc", "xyz", -1)]           // String comparison fallback
    [InlineData("xyz", "abc", 1)]            // String comparison fallback
    public void Compare_ShouldFallbackToStringComparison_ForInvalidVersions(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0);
        else if (expected > 0)
            Assert.True(result > 0);
        else
            Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("1.0", "1.0.0")]
    public void Compare_ShouldHandleDifferentVersionFormats(string version1, string version2)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        // Result depends on Version.TryParse behavior
        // This test ensures no exceptions are thrown
        Assert.NotNull(result);
    }

    [Fact]
    public void Compare_WithList_ShouldSortCorrectly()
    {
        // Arrange
        var list = new List<InstanceData>
        {
            CreateInstanceData("2.0.0"),
            CreateInstanceData("1.0.0"),
            CreateInstanceData("1.5.0"),
            CreateInstanceData("1.0.1"),
            CreateInstanceData("3.0.0")
        };

        // Act
        list.Sort(_comparer);

        // Assert
        Assert.Equal("1.0.0", list[0].Version);
        Assert.Equal("1.0.1", list[1].Version);
        Assert.Equal("1.5.0", list[2].Version);
        Assert.Equal("2.0.0", list[3].Version);
        Assert.Equal("3.0.0", list[4].Version);
    }

    [Fact]
    public void Compare_WithOrderBy_ShouldSortCorrectly()
    {
        // Arrange
        var list = new List<InstanceData>
        {
            CreateInstanceData("2.1.0"),
            CreateInstanceData("1.0.0"),
            CreateInstanceData("2.0.0"),
            CreateInstanceData("1.5.0")
        };

        // Act
        var sorted = list.OrderBy(x => x, _comparer).ToList();

        // Assert
        Assert.Equal("1.0.0", sorted[0].Version);
        Assert.Equal("1.5.0", sorted[1].Version);
        Assert.Equal("2.0.0", sorted[2].Version);
        Assert.Equal("2.1.0", sorted[3].Version);
    }

    [Fact]
    public void Compare_WithOrderByDescending_ShouldSortCorrectly()
    {
        // Arrange
        var list = new List<InstanceData>
        {
            CreateInstanceData("1.0.0"),
            CreateInstanceData("2.1.0"),
            CreateInstanceData("1.5.0"),
            CreateInstanceData("2.0.0")
        };

        // Act
        var sorted = list.OrderByDescending(x => x, _comparer).ToList();

        // Assert
        Assert.Equal("2.1.0", sorted[0].Version);
        Assert.Equal("2.0.0", sorted[1].Version);
        Assert.Equal("1.5.0", sorted[2].Version);
        Assert.Equal("1.0.0", sorted[3].Version);
    }

    [Fact]
    public void Compare_ShouldBeConsistent()
    {
        // Arrange
        var data1 = CreateInstanceData("1.0.0");
        var data2 = CreateInstanceData("2.0.0");

        // Act
        var result1 = _comparer.Compare(data1, data2);
        var result2 = _comparer.Compare(data2, data1);

        // Assert
        Assert.True(result1 < 0);
        Assert.True(result2 > 0);
        Assert.Equal(-Math.Sign(result1), Math.Sign(result2));
    }

    [Theory]
    [InlineData("10.0.0", "2.0.0", 1)]  // 10 > 2 numerically
    [InlineData("1.10.0", "1.2.0", 1)]  // 10 > 2 numerically
    [InlineData("1.0.10", "1.0.2", 1)]  // 10 > 2 numerically
    public void Compare_ShouldUseNumericComparison_NotLexicographic(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0);
        else if (expected > 0)
            Assert.True(result > 0);
        else
            Assert.Equal(0, result);
    }

    [Fact]
    public void Compare_WithMixedValidAndInvalidVersions_ShouldNotThrow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var list = new List<InstanceData>
        {
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), "1.0.0"),
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), "invalid"),
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), "2.0.0"),
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{}"), "another-invalid")
        };

        // Act & Assert - Should not throw
        var sorted = list.OrderBy(x => x, _comparer).ToList();
        Assert.Equal(4, sorted.Count);
    }

    [Fact]
    public void Compare_ShouldCompareByHistorySequence_WhenVersionsAreEqual()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var data1 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");
        var data2 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0");
        var data3 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0");

        // Act & Assert
        // data1 (seq=0) < data2 (seq=1)
        Assert.True(_comparer.Compare(data1, data2) < 0);
        // data2 (seq=1) < data3 (seq=2)
        Assert.True(_comparer.Compare(data2, data3) < 0);
        // data1 (seq=0) < data3 (seq=2)
        Assert.True(_comparer.Compare(data1, data3) < 0);
    }

    [Fact]
    public void Compare_ShouldPrioritizeVersion_OverHistorySequence()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        // Version 2.0.0 with sequence 0
        var data1 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "2.0.0");
        
        // Version 1.0.0 with higher sequence numbers
        var data2 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0");
        var data3 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0");
        var data4 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "1.0.0");

        // Act & Assert
        // Version 2.0.0 (seq=0) > Version 1.0.0 (seq=2) - Version takes priority
        Assert.True(_comparer.Compare(data1, data4) > 0);
    }

    [Fact]
    public void Compare_WithMixedVersionsAndSequences_ShouldSortCorrectly()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var list = new List<InstanceData>
        {
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "2.0.0"), // seq=0
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0"), // seq=0
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0"), // seq=1
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "2.0.0"), // seq=1
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":5}"), "1.0.0"), // seq=2
        };

        // Act
        var sorted = list.OrderBy(x => x, _comparer).ToList();

        // Assert - Should be sorted by version first, then by sequence
        Assert.Equal("1.0.0", sorted[0].Version);
        Assert.Equal(0, sorted[0].HistorySequence);
        
        Assert.Equal("1.0.0", sorted[1].Version);
        Assert.Equal(1, sorted[1].HistorySequence);
        
        Assert.Equal("1.0.0", sorted[2].Version);
        Assert.Equal(2, sorted[2].HistorySequence);
        
        Assert.Equal("2.0.0", sorted[3].Version);
        Assert.Equal(0, sorted[3].HistorySequence);
        
        Assert.Equal("2.0.0", sorted[4].Version);
        Assert.Equal(1, sorted[4].HistorySequence);
    }

    [Fact]
    public void Compare_WithDescending_ShouldRespectVersionAndSequence()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var list = new List<InstanceData>
        {
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0"), // seq=0
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0"), // seq=1
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "2.0.0"), // seq=0
            instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "2.0.0"), // seq=1
        };

        // Act
        var sorted = list.OrderByDescending(x => x, _comparer).ToList();

        // Assert - Descending: highest version first, highest sequence within same version
        Assert.Equal("2.0.0", sorted[0].Version);
        Assert.Equal(1, sorted[0].HistorySequence);
        
        Assert.Equal("2.0.0", sorted[1].Version);
        Assert.Equal(0, sorted[1].HistorySequence);
        
        Assert.Equal("1.0.0", sorted[2].Version);
        Assert.Equal(1, sorted[2].HistorySequence);
        
        Assert.Equal("1.0.0", sorted[3].Version);
        Assert.Equal(0, sorted[3].HistorySequence);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]  // Same version - will compare by auto-generated sequence
    [InlineData("2.0.0", "1.0.0", 1)]  // Version priority: 2.0.0 > 1.0.0
    [InlineData("1.0.0", "2.0.0", -1)] // Version priority: 1.0.0 < 2.0.0
    [InlineData("1.5.0", "1.2.0", 1)]  // 1.5.0 > 1.2.0
    [InlineData("3.0.0", "3.0.1", -1)] // 3.0.0 < 3.0.1
    public void Compare_ShouldHandleVersionCombinations(
        string version1,
        string version2,
        int expectedSign)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);
        
        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expectedSign < 0)
            Assert.True(result < 0, $"Expected {version1} < {version2}");
        else if (expectedSign > 0)
            Assert.True(result > 0, $"Expected {version1} > {version2}");
        else
            Assert.Equal(0, result);
    }

    #region Extended Version Format Tests (MAJOR.MINOR.PATCH-pkg.PKG_VERSION+PKG_NAME)

    [Theory]
    [InlineData("1.0.0-pkg.1.2.1+account", "1.0.0-pkg.1.2.2+account", -1)] // Same artifact, lower pkg version
    [InlineData("1.0.0-pkg.1.2.2+account", "1.0.0-pkg.1.2.1+account", 1)]  // Same artifact, higher pkg version
    [InlineData("1.0.0-pkg.1.2.0+account", "1.0.0-pkg.1.2.0+account", 0)]  // Same artifact and pkg version
    [InlineData("2.0.0-pkg.1.3.0+account", "1.0.0-pkg.1.2.2+account", 1)]  // Higher artifact version wins
    [InlineData("1.0.0-pkg.1.2.2+account", "2.0.0-pkg.1.3.0+account", -1)] // Lower artifact version loses
    [InlineData("2.0.0-pkg.1.3.1+account", "2.0.0-pkg.1.3.0+account", 1)]  // Same artifact, higher pkg version
    public void Compare_ShouldHandleExtendedVersionFormat(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {version1} < {version2}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {version1} > {version2}");
        else
            Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("1.0.0-pkg.1.2.0+account", "1.0.0-pkg.1.2.0+different", 0)] // Build metadata doesn't affect comparison
    [InlineData("1.0.0-pkg.1.2.0+aaa", "1.0.0-pkg.1.2.0+zzz", 0)]           // Build metadata doesn't affect comparison
    [InlineData("2.0.0-pkg.1.0.0+app1", "1.0.0-pkg.9.9.9+app2", 1)]         // Artifact version takes priority
    public void Compare_ShouldIgnoreBuildMetadata(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {version1} < {version2}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {version1} > {version2}");
        else
            Assert.Equal(0, result);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0-pkg.1.0.0+account", -1)] // Simple version < extended with pkg
    [InlineData("1.0.0-pkg.1.0.0+account", "1.0.0", 1)]  // Extended with pkg > simple version
    [InlineData("2.0.0", "1.0.0-pkg.9.9.9+account", 1)]  // Higher artifact wins
    public void Compare_ShouldHandleMixedSimpleAndExtendedVersions(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {version1} < {version2}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {version1} > {version2}");
        else
            Assert.Equal(0, result);
    }

    [Fact]
    public void Compare_WithExtendedVersionList_ShouldSortCorrectly()
    {
        // Arrange
        var list = new List<InstanceData>
        {
            CreateInstanceData("1.0.0-pkg.1.2.2+account"),
            CreateInstanceData("1.0.0-pkg.1.2.0+account"),
            CreateInstanceData("2.0.0-pkg.1.3.0+account"),
            CreateInstanceData("1.0.0-pkg.1.2.1+account"),
            CreateInstanceData("2.0.0-pkg.1.3.1+account")
        };

        // Act
        var sorted = list.OrderBy(x => x, _comparer).ToList();

        // Assert - Should be sorted by artifact version first, then by pkg version
        Assert.Equal("1.0.0-pkg.1.2.0+account", sorted[0].Version);
        Assert.Equal("1.0.0-pkg.1.2.1+account", sorted[1].Version);
        Assert.Equal("1.0.0-pkg.1.2.2+account", sorted[2].Version);
        Assert.Equal("2.0.0-pkg.1.3.0+account", sorted[3].Version);
        Assert.Equal("2.0.0-pkg.1.3.1+account", sorted[4].Version);
    }

    [Fact]
    public void Compare_WithExtendedVersionDescending_ShouldReturnHighestFirst()
    {
        // Arrange
        var list = new List<InstanceData>
        {
            CreateInstanceData("1.0.0-pkg.1.2.0+account"),
            CreateInstanceData("2.0.0-pkg.1.3.0+account"),
            CreateInstanceData("1.0.0-pkg.1.2.2+account"),
            CreateInstanceData("2.0.0-pkg.1.3.1+account"),
            CreateInstanceData("1.0.0-pkg.1.2.1+account")
        };

        // Act
        var sorted = list.OrderByDescending(x => x, _comparer).ToList();

        // Assert - Highest version first
        Assert.Equal("2.0.0-pkg.1.3.1+account", sorted[0].Version);
        Assert.Equal("2.0.0-pkg.1.3.0+account", sorted[1].Version);
        Assert.Equal("1.0.0-pkg.1.2.2+account", sorted[2].Version);
        Assert.Equal("1.0.0-pkg.1.2.1+account", sorted[3].Version);
        Assert.Equal("1.0.0-pkg.1.2.0+account", sorted[4].Version);
    }

    [Theory]
    [InlineData("1.0.0-pkg.1.2.0+account", true)]
    [InlineData("2.0.0-pkg.10.20.30+myapp", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0+metadata", false)]
    [InlineData("invalid", false)]
    public void HasPackageVersion_ShouldDetectPackageVersionCorrectly(string version, bool expected)
    {
        // Act
        var result = InstanceDataVersionComparer.HasPackageVersion(version);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.0.0-pkg.1.2.0+account", "1.0.0")]
    [InlineData("2.5.3-pkg.10.20.30+myapp", "2.5.3")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0+metadata", "1.0.0")]
    public void GetArtifactVersion_ShouldExtractArtifactVersionCorrectly(string fullVersion, string expectedArtifact)
    {
        // Act
        var result = InstanceDataVersionComparer.GetArtifactVersion(fullVersion);

        // Assert
        Assert.Equal(expectedArtifact, result);
    }

    [Fact]
    public void ParseVersion_ShouldParseExtendedFormatCorrectly()
    {
        // Arrange & Act
        var parsed = InstanceDataVersionComparer.ParseVersion("1.2.3-pkg.4.5.6+myapp");

        // Assert
        Assert.Equal("1.2.3", parsed.ArtifactVersion);
        Assert.Equal("4.5.6", parsed.PackageVersion);
    }

    [Fact]
    public void ParseVersion_ShouldParseSimpleVersionCorrectly()
    {
        // Arrange & Act
        var parsed = InstanceDataVersionComparer.ParseVersion("1.2.3");

        // Assert
        Assert.Equal("1.2.3", parsed.ArtifactVersion);
        Assert.Null(parsed.PackageVersion);
    }

    [Fact]
    public void ParseVersion_ShouldHandleVersionWithOnlyMetadata()
    {
        // Arrange & Act
        var parsed = InstanceDataVersionComparer.ParseVersion("1.2.3+metadata");

        // Assert
        Assert.Equal("1.2.3", parsed.ArtifactVersion);
        Assert.Null(parsed.PackageVersion);
    }

    #endregion

    #region Pre-Release Version Format Tests

    [Theory]
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "1.0.0-alpha.1", "1.17.0")]
    [InlineData("1.0.0-beta-pkg.1.2.0+customer", "1.0.0-beta", "1.2.0")]
    [InlineData("2.0.0-rc.1-pkg.2.5.1+myapp", "2.0.0-rc.1", "2.5.1")]
    [InlineData("1.0.0-alpha.1.2-pkg.1.0.0+test", "1.0.0-alpha.1.2", "1.0.0")]
    public void ParseVersion_ShouldParsePreReleaseVersionCorrectly(string version, string expectedArtifact, string expectedPackage)
    {
        // Act
        var parsed = InstanceDataVersionComparer.ParseVersion(version);

        // Assert
        Assert.Equal(expectedArtifact, parsed.ArtifactVersion);
        Assert.Equal(expectedPackage, parsed.PackageVersion);
    }

    [Theory]
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "1.0.0-alpha.1-pkg.1.17.0+different", 0)] // Same version, different metadata
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "1.0.0-alpha.1-pkg.1.18.0+account", -1)] // Same artifact, lower pkg
    [InlineData("1.0.0-alpha.1-pkg.1.18.0+account", "1.0.0-alpha.1-pkg.1.17.0+account", 1)]  // Same artifact, higher pkg
    [InlineData("1.0.0-beta-pkg.1.0.0+account", "1.0.0-alpha-pkg.1.0.0+account", 1)]         // beta > alpha (string comparison)
    [InlineData("2.0.0-alpha.1-pkg.1.0.0+account", "1.0.0-pkg.1.0.0+account", 1)]            // 2.0.0-alpha.1 > 1.0.0
    public void Compare_ShouldHandlePreReleaseVersions(string version1, string version2, int expected)
    {
        // Arrange
        var data1 = CreateInstanceData(version1);
        var data2 = CreateInstanceData(version2);

        // Act
        var result = _comparer.Compare(data1, data2);

        // Assert
        if (expected < 0)
            Assert.True(result < 0, $"Expected {version1} < {version2}");
        else if (expected > 0)
            Assert.True(result > 0, $"Expected {version1} > {version2}");
        else
            Assert.Equal(0, result);
    }

    [Fact]
    public void Compare_WithPreReleaseVersionList_ShouldSortCorrectly()
    {
        // Arrange
        var list = new List<InstanceData>
        {
            CreateInstanceData("1.0.0-alpha.1-pkg.1.17.0+account"),
            CreateInstanceData("1.0.0-pkg.1.17.0+account"),
            CreateInstanceData("1.0.0-beta-pkg.1.17.0+account"),
            CreateInstanceData("1.0.0-alpha.1-pkg.1.18.0+account"),
            CreateInstanceData("2.0.0-rc.1-pkg.1.0.0+account")
        };

        // Act
        var sorted = list.OrderByDescending(x => x, _comparer).ToList();

        // Assert - Higher versions first
        Assert.StartsWith("2.0.0", sorted[0].Version); // 2.0.0-rc.1 is highest
    }

    [Theory]
    [InlineData("1.0.0-pkg.1.17.0+account+build.123", "1.0.0", "1.17.0")] // Multiple build metadata
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account+build.456", "1.0.0-alpha.1", "1.17.0")] // Pre-release with multiple metadata
    public void ParseVersion_ShouldHandleMultipleBuildMetadata(string version, string expectedArtifact, string expectedPackage)
    {
        // Act
        var parsed = InstanceDataVersionComparer.ParseVersion(version);

        // Assert
        Assert.Equal(expectedArtifact, parsed.ArtifactVersion);
        Assert.Equal(expectedPackage, parsed.PackageVersion);
    }

    [Theory]
    [InlineData("1.0.0-alpha.1-pkg.1.17.0+account", "1.0.0-alpha.1")]
    [InlineData("1.0.0-beta.2-pkg.1.2.0+customer", "1.0.0-beta.2")]
    [InlineData("2.0.0-rc.1-pkg.2.5.1+myapp", "2.0.0-rc.1")]
    public void GetArtifactVersion_ShouldExtractPreReleaseArtifact(string fullVersion, string expectedArtifact)
    {
        // Act
        var result = InstanceDataVersionComparer.GetArtifactVersion(fullVersion);

        // Assert
        Assert.Equal(expectedArtifact, result);
    }

    #endregion

    #region Package Version Only Matching Tests

    [Fact]
    public void MatchesPackage_ShouldMatchPackageVersionPart()
    {
        // Arrange & Act & Assert
        Assert.True(InstanceDataVersionComparer.MatchesPackage("1.0.0-pkg.00.01.417+onboarding", "00.01.417"));
        Assert.True(InstanceDataVersionComparer.MatchesPackage("1.0.0-pkg.1.17.0+account", "1.17.0"));
    }

    [Fact]
    public void MatchesPackage_ShouldNormalizeLeadingZeros()
    {
        Assert.True(InstanceDataVersionComparer.MatchesPackage("1.0.0-pkg.00.01.417+onboarding", "0.1.417"));
        Assert.True(InstanceDataVersionComparer.MatchesPackage("1.0.0-pkg.0.1.417+onboarding", "00.01.417"));
    }

    [Fact]
    public void MatchesPackage_ShouldReturnFalse_WhenNoPackageVersion()
    {
        Assert.False(InstanceDataVersionComparer.MatchesPackage("1.0.0", "1.0.0"));
        Assert.False(InstanceDataVersionComparer.MatchesPackage("1.0.0+metadata", "1.0.0"));
    }

    [Fact]
    public void MatchesPackage_ShouldReturnFalse_WhenPackageVersionDoesNotMatch()
    {
        Assert.False(InstanceDataVersionComparer.MatchesPackage("1.0.0-pkg.1.17.0+account", "2.0.0"));
        Assert.False(InstanceDataVersionComparer.MatchesPackage("1.0.0-pkg.00.01.417+onboarding", "00.02.417"));
    }

    [Fact]
    public void FindBestMatch_ShouldMatchByPackageVersion_WhenNoArtifactMatch()
    {
        // Arrange - DB'deki versiyonlar
        var versions = new List<string>
        {
            "1.0.0-pkg.00.01.417+onboarding",
            "1.0.0-pkg.00.01.418+onboarding",
            "2.0.0-pkg.00.02.100+account"
        };

        // Act - İstekteki versiyon sadece package version kısmı
        var result = InstanceDataVersionComparer.FindBestMatch(versions, "00.01.417");

        // Assert
        Assert.Equal("1.0.0-pkg.00.01.417+onboarding", result);
    }

    [Fact]
    public void FindBestMatch_ShouldReturnHighestVersion_WhenMultiplePackageMatches()
    {
        // Arrange - Farklı artifact versiyonları aynı package version ile
        var versions = new List<string>
        {
            "1.0.0-pkg.00.01.417+onboarding",
            "2.0.0-pkg.00.01.417+account"
        };

        // Act
        var result = InstanceDataVersionComparer.FindBestMatch(versions, "00.01.417");

        // Assert - En yüksek versiyon dönmeli
        Assert.Equal("2.0.0-pkg.00.01.417+account", result);
    }

    [Fact]
    public void FindBestMatch_ShouldPreferArtifactMatch_OverPackageMatch()
    {
        // Arrange - "1.0.0" hem artifact hem de başka bir versiyonun package'ı olabilir
        var versions = new List<string>
        {
            "1.0.0-pkg.1.17.0+account",
            "2.0.0-pkg.1.0.0+other"
        };

        // Act - "1.0.0" artifact version olarak match bulmalı
        var result = InstanceDataVersionComparer.FindBestMatch(versions, "1.0.0");

        // Assert - Artifact match öncelikli olmalı
        Assert.Equal("1.0.0-pkg.1.17.0+account", result);
    }

    [Fact]
    public void FindBestMatch_ShouldMatchPackageVersion_WithLeadingZerosNormalized()
    {
        // Arrange
        var versions = new List<string>
        {
            "1.0.0-pkg.00.01.417+onboarding"
        };

        // Act - Leading zero farkı olan aynı versiyon
        var result = InstanceDataVersionComparer.FindBestMatch(versions, "0.1.417");

        // Assert
        Assert.Equal("1.0.0-pkg.00.01.417+onboarding", result);
    }

    [Fact]
    public void FindBestMatch_ShouldReturnNull_WhenNoPackageMatch()
    {
        // Arrange
        var versions = new List<string>
        {
            "1.0.0-pkg.00.01.417+onboarding"
        };

        // Act
        var result = InstanceDataVersionComparer.FindBestMatch(versions, "99.99.999");

        // Assert
        Assert.Null(result);
    }

    #endregion

    private InstanceData CreateInstanceData(string version)
    {
        var instance = InstanceFactory.CreateDefault();
        return instance.AddDataWithVersion(
            Guid.NewGuid(),
            JsonData.CreateFrom("{}"),
            version
        );
    }
}

