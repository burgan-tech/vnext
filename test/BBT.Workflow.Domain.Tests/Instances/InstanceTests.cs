using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Instances;

public class InstanceTests : DomainTestBase<DomainEntryPoint>
{

    [Fact]
    public void Constructor_ShouldInitializeProperties()
    {
        // Arrange
        var id = Guid.NewGuid();
        var key = "TestKey";
        var flow = "sys-flows";

        // Act
        var instance = Instance.Create(id, flow, key);

        // Assert
        Assert.Equal(id, instance.Id);
        Assert.Equal(key, instance.Key);
        Assert.Equal(InstanceStatus.Active, instance.Status);
        Assert.NotNull(instance.Tags);
        Assert.Empty(instance.Tags);
    }

    [Fact]
    public void ChangeState_ShouldUpdateCurrentState_FromState()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var state = StateFactory.CreateDefault("next-state");

        // Act
        instance.ChangeState(state);

        // Assert
        Assert.Equal("next-state", instance.CurrentState);
    }

    [Fact]
    public void Complete_ShouldSetCompletedAtAndStatus()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.CreatedAt = DateTime.UtcNow.AddMinutes(-5);

        // Act
        instance.Complete("test-domain");

        // Assert
        Assert.Equal(InstanceStatus.Completed, instance.Status);
        Assert.NotNull(instance.CompletedAt);
        Assert.Equal(instance.CompletedAt - instance.CreatedAt, instance.Duration);
    }

    [Fact]
    public void AddTags_ShouldReplaceAndAddNewTags()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddTags(new[] { "tag1", "tag2" });

        // Act
        instance.AddTags(new[] { "tag2", "tag3" });

        // Assert
        Assert.Equal(2, instance.Tags.Count);
        Assert.Contains("tag2", instance.Tags);
        Assert.Contains("tag3", instance.Tags);
        Assert.DoesNotContain("tag1", instance.Tags);
    }

    [Fact]
    public void AddData_ShouldAddInitialData_WhenNoPreviousData()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var dataId = Guid.NewGuid();
        var input = JsonData.CreateFrom("{}");

        // Act
        var result = instance.AddData(dataId, input);

        // Assert
        Assert.Single(instance.DataList);
        Assert.Equal(dataId, result.Id);
        Assert.Equal(WorkflowConstants.DefaultVersion, result.Version);
        Assert.Equal(input.JsonElement.ToString(), result.Data.JsonElement.ToString());
    }

    [Fact]
    public void AddData_ShouldCreateNewVersion_WhenPreviousDataExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var data1 = JsonData.CreateFrom("{\"key\":\"value1\"}");
        var data2 = JsonData.CreateFrom("{\"key\":\"value2\"}");

        instance.AddData(Guid.NewGuid(), data1);
        var newData = instance.AddData(Guid.NewGuid(), data2, VersionStrategy.IncreaseMinor);

        // Assert
        Assert.Equal(2, instance.DataList.Count);
        Assert.StartsWith("1.1.", newData.Version); // increase minor
    }

    [Fact]
    public void FindData_ShouldReturnExactMatch_WhenVersionExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var input = JsonData.CreateFrom("{}");
        var version = "1.0.0";

        instance.AddData(Guid.NewGuid(), input);
        var result = instance.FindData(version);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(version, result.Version);
    }

    [Fact]
    public void FindData_ShouldReturnLatestPartialMatch_WhenPartialVersionGiven()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // NewVersion metodu data merge yaptığı için farklı keyler kullanarak 
        // her seferinde gerçekten farklı data oluşturuyoruz
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v1\":1}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v2\":2}"), "1.0.1");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v3\":3}"), "1.0.2");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v4\":4}"), "1.1.0");

        // Act
        var result = instance.FindData("1.0");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0.2", result.Version);
    }

    #region FindData with Extended Version Format Tests

    [Fact]
    public void FindData_ShouldReturnExactMatch_WhenExtendedVersionExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var version = "1.0.0-pkg.1.17.0+account";
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), version);

        // Act
        var result = instance.FindData(version);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(version, result.Version);
    }

    [Fact]
    public void FindData_ShouldReturnHighestPkgVersion_WhenArtifactVersionOnly()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Add multiple versions with same artifact version but different pkg versions
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.17.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-pkg.1.18.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0-pkg.1.16.0+account");

        // Act - Client sends only artifact version
        var result = instance.FindData("1.0.0");

        // Assert - Should return highest pkg version for that artifact
        Assert.NotNull(result);
        Assert.Equal("1.0.0-pkg.1.18.0+account", result.Version);
    }

    [Fact]
    public void FindData_ShouldReturnCorrectVersion_WhenMultipleArtifactVersionsExist()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.2.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-pkg.1.2.1+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "2.0.0-pkg.1.3.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "2.0.0-pkg.1.3.1+account");

        // Act - Query for artifact version 1.0.0
        var result1 = instance.FindData("1.0.0");
        // Query for artifact version 2.0.0
        var result2 = instance.FindData("2.0.0");

        // Assert
        Assert.NotNull(result1);
        Assert.Equal("1.0.0-pkg.1.2.1+account", result1.Version);

        Assert.NotNull(result2);
        Assert.Equal("2.0.0-pkg.1.3.1+account", result2.Version);
    }

    [Fact]
    public void FindData_ShouldReturnNull_WhenArtifactVersionNotFound()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.2.0+account");

        // Act
        var result = instance.FindData("2.0.0");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindData_ShouldHandlePartialVersion_WithExtendedFormat()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.2.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.1-pkg.1.2.1+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.1.0-pkg.1.3.0+account");

        // Act - Partial version "1.0" should find highest matching
        var result = instance.FindData("1.0");

        // Assert
        Assert.NotNull(result);
        // Should find highest version starting with 1.0.x
        Assert.StartsWith("1.0.", InstanceDataVersionComparer.GetArtifactVersion(result.Version));
    }

    [Fact]
    public void FindData_ShouldReturnSimpleVersion_WhenNoExtendedVersionExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");

        // Act
        var result = instance.FindData("1.0.0");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public void FindData_ShouldPreferExactMatch_OverPkgVersionLookup()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Add a simple version and an extended version with same artifact
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-pkg.1.2.0+account");

        // Act - Query for exact simple version
        var result = instance.FindData("1.0.0");

        // Assert - Should return the exact match (simple version)
        Assert.NotNull(result);
        Assert.Equal("1.0.0", result.Version);
    }

    [Fact]
    public void FindData_ShouldFindPreReleaseArtifactVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-alpha.1-pkg.1.17.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-alpha.1-pkg.1.18.0+account");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0-pkg.1.0.0+account");

        // Act - Query for pre-release artifact version
        var result = instance.FindData("1.0.0-alpha.1");

        // Assert - Should return highest pkg version for 1.0.0-alpha.1
        Assert.NotNull(result);
        Assert.Equal("1.0.0-alpha.1-pkg.1.18.0+account", result.Version);
    }

    [Fact]
    public void FindData_ShouldFindExactPreReleaseVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        var exactVersion = "1.0.0-beta.2-pkg.1.5.0+customer";
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), exactVersion);
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0-beta.2-pkg.1.6.0+customer");

        // Act - Query for exact version
        var result = instance.FindData(exactVersion);

        // Assert - Should return exact match
        Assert.NotNull(result);
        Assert.Equal(exactVersion, result.Version);
    }

    [Fact]
    public void FindData_ShouldHandleMultipleBuildMetadata()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0-pkg.1.17.0+account+build.123");

        // Act - Query for artifact version
        var result = instance.FindData("1.0.0");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("1.0.0-pkg.1.17.0+account+build.123", result.Version);
    }

    #endregion

    [Fact]
    public void SetKey_ShouldUpdateKey_WhenValid()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var newKey = "updated-key";

        // Act
        instance.SetKey(newKey);

        // Assert
        Assert.Equal(newKey, instance.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void SetKey_ShouldThrow_WhenInvalid(string? key)
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => instance.SetKey(key!));
    }

    [Fact]
    public void LatestData_ShouldReturnMostRecentVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // NewVersion metodu data merge yaptığı için farklı keyler kullanarak 
        // her seferinde gerçekten farklı data oluşturuyoruz
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v1\":1}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v2\":2}"), "1.0.1");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v3\":3}"), "1.0.2");

        // Act
        var latest = instance.LatestData;

        // Assert
        Assert.NotNull(latest);
        Assert.Equal("1.0.2", latest.Version);
    }

    [Fact]
    public void AddData_ShouldNotCreateNewVersion_WhenDataIsSame()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var dataId1 = Guid.NewGuid();
        var dataId2 = Guid.NewGuid();
        var sameData = JsonData.CreateFrom("{\"key\":\"value\"}");
        
        // Act
        var result1 = instance.AddData(dataId1, sameData);
        var result2 = instance.AddData(dataId2, sameData);
        
        // Assert
        Assert.Single(instance.DataList);
        Assert.Equal(result1.Id, result2.Id); // Same instance returned
        Assert.Equal(result1.Version, result2.Version);
        Assert.Equal(result1.DataHash, result2.DataHash);
    }

    [Fact]
    public void AddData_ShouldCreateNewVersion_WhenDataIsDifferent()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var dataId1 = Guid.NewGuid();
        var dataId2 = Guid.NewGuid();
        var data1 = JsonData.CreateFrom("{\"key\":\"value1\"}");
        var data2 = JsonData.CreateFrom("{\"key\":\"value2\"}");
        
        // Act
        var result1 = instance.AddData(dataId1, data1);
        var result2 = instance.AddData(dataId2, data2);
        
        // Assert
        Assert.Equal(2, instance.DataList.Count);
        Assert.NotEqual(result1.Id, result2.Id);
        Assert.NotEqual(result1.Version, result2.Version);
        Assert.NotEqual(result1.DataHash, result2.DataHash);
        Assert.False(result1.IsLatest);
        Assert.True(result2.IsLatest);
    }

    [Fact]
    public void AddDataWithVersion_ShouldNotCreateNewVersion_WhenDataIsSame()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var dataId1 = Guid.NewGuid();
        var dataId2 = Guid.NewGuid();
        var sameData = JsonData.CreateFrom("{\"key\":\"value\"}");
        
        // Act
        var result1 = instance.AddDataWithVersion(dataId1, sameData, "1.0.0");
        var result2 = instance.AddDataWithVersion(dataId2, sameData, "2.0.0");
        
        // Assert
        Assert.Single(instance.DataList);
        Assert.Equal(result1.Id, result2.Id); // Same instance returned
        Assert.Equal("1.0.0", result2.Version); // Original version maintained
        Assert.Equal(result1.DataHash, result2.DataHash);
    }

    [Fact]
    public void AddDataWithVersion_ShouldCreateNewVersion_WhenDataIsDifferent()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var dataId1 = Guid.NewGuid();
        var dataId2 = Guid.NewGuid();
        var data1 = JsonData.CreateFrom("{\"key\":\"value1\"}");
        var data2 = JsonData.CreateFrom("{\"key\":\"value2\"}");
        
        // Act
        var result1 = instance.AddDataWithVersion(dataId1, data1, "1.0.0");
        var result2 = instance.AddDataWithVersion(dataId2, data2, "2.0.0");
        
        // Assert
        Assert.Equal(2, instance.DataList.Count);
        Assert.NotEqual(result1.Id, result2.Id);
        Assert.Equal("1.0.0", result1.Version);
        Assert.Equal("2.0.0", result2.Version);
        Assert.NotEqual(result1.DataHash, result2.DataHash);
        Assert.False(result1.IsLatest);
        Assert.True(result2.IsLatest);
    }

    [Fact]
    public void InstanceData_HasSameData_ShouldReturnTrue_WhenDataIsIdentical()
    {
        // Arrange
        var data1 = JsonData.CreateFrom("{\"key\":\"value\"}");
        var data2 = JsonData.CreateFrom("{\"key\":\"value\"}");
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.AddDataWithVersion(Guid.NewGuid(), data1, "1.0.0");
        
        // Act
        var result = instanceData.HasSameData(data2);
        
        // Assert
        Assert.True(result);
    }

    [Fact]
    public void InstanceData_HasSameData_ShouldReturnFalse_WhenDataIsDifferent()
    {
        // Arrange
        var data1 = JsonData.CreateFrom("{\"key\":\"value1\"}");
        var data2 = JsonData.CreateFrom("{\"key\":\"value2\"}");
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.AddDataWithVersion(Guid.NewGuid(), data1, "1.0.0");
        
        // Act
        var result = instanceData.HasSameData(data2);
        
        // Assert
        Assert.False(result);
    }

    [Fact]
    public void InstanceData_HasSameData_ShouldReturnTrue_WithDifferentJsonFormatting()
    {
        // Arrange - Same semantic content but different formatting
        var data1 = JsonData.CreateFrom("{\"key\":\"value\",\"number\":123}");
        var data2 = JsonData.CreateFrom("{ \"number\": 123, \"key\": \"value\" }"); // Different order & spacing
        var instance = InstanceFactory.CreateDefault();
        var instanceData = instance.AddDataWithVersion(Guid.NewGuid(), data1, "1.0.0");
        
        // Act
        var result = instanceData.HasSameData(data2);
        
        // Assert
        Assert.True(result); // Should be true because semantic content is same
    }

    [Fact]
    public void AddData_ShouldNotCreateNewVersion_WithDifferentJsonFormatting()
    {
        // Arrange - Same semantic content but different formatting
        var instance = InstanceFactory.CreateDefault();
        var dataId1 = Guid.NewGuid();
        var dataId2 = Guid.NewGuid();
        var data1 = JsonData.CreateFrom("{\"key\":\"value\",\"items\":[1,2,3]}");
        var data2 = JsonData.CreateFrom("{ \"items\": [1, 2, 3], \"key\": \"value\" }"); // Different formatting
        
        // Act
        var result1 = instance.AddData(dataId1, data1);
        var result2 = instance.AddData(dataId2, data2);
        
        // Assert
        Assert.Single(instance.DataList);
        Assert.Equal(result1.Id, result2.Id); // Same instance returned
        Assert.Equal(result1.DataHash, result2.DataHash); // Same hash
    }

    [Fact]
    public void JsonData_NormalizedJson_ShouldReturnConsistentFormat()
    {
        // Arrange - Same semantic content but different formatting
        var json1 = JsonData.CreateFrom("{\"key\":\"value\",\"items\":[1,2,3]}");
        var json2 = JsonData.CreateFrom("{ \"items\": [1, 2, 3], \"key\": \"value\" }");
        var json3 = JsonData.CreateFrom("{\n  \"key\": \"value\",\n  \"items\": [\n    1,\n    2,\n    3\n  ]\n}");
        
        // Act
        var normalized1 = json1.NormalizedJson;
        var normalized2 = json2.NormalizedJson;
        var normalized3 = json3.NormalizedJson;
        
        // Assert
        Assert.Equal(normalized1, normalized2);
        Assert.Equal(normalized2, normalized3);
        Assert.Equal(normalized1, normalized3);
        
        // Verify it's deterministic (property order should be consistent)
        Assert.Contains("\"items\"", normalized1);
        Assert.Contains("\"key\"", normalized1);
    }

    [Fact]
    public void LatestData_ShouldReturnCorrectResult_DuringConcurrentAccess()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        const int threadCount = 5;
        var barrier = new Barrier(threadCount);
        var tasks = new List<Task>();
        var latestDataResults = new ConcurrentBag<InstanceData?>();

        // Act - Add data and read LatestData concurrently
        for (int i = 0; i < threadCount; i++)
        {
            int threadIndex = i;
            var task = Task.Run(() =>
            {
                // Add some data first
                var data = JsonData.CreateFrom($"{{\"thread\":{threadIndex}}}");
                instance.AddData(Guid.NewGuid(), data, VersionStrategy.IncreasePatch);
                
                // Synchronize all threads
                barrier.SignalAndWait();
                
                // All threads read LatestData simultaneously
                var latestData = instance.LatestData;
                latestDataResults.Add(latestData);
            });
            tasks.Add(task);
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        Assert.Equal(threadCount, instance.DataList.Count);
        Assert.Equal(threadCount, latestDataResults.Count);
        
        // All results should be valid (not null)
        Assert.All(latestDataResults, result => Assert.NotNull(result));
        
        // Latest data should be consistently returned
        var actualLatest = instance.LatestData;
        Assert.NotNull(actualLatest);
    }

    [Fact]
    public void FindData_ShouldWorkCorrectly_DuringConcurrentAddOperations()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var addTasks = new List<Task>();
        var findTasks = new List<Task<InstanceData?>>();
        
        // Add initial data
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{}"), VersionStrategy.IncreasePatch); // 1.0.1

        // Act - Concurrent add and find operations
        for (int i = 0; i < 5; i++)
        {
            // Add operations
            var addTask = Task.Run(() =>
            {
                for (int j = 0; j < 3; j++)
                {
                    var data = JsonData.CreateFrom($"{{\"operation\":{j}}}");
                    instance.AddData(Guid.NewGuid(), data, VersionStrategy.IncreasePatch);
                }
            });
            addTasks.Add(addTask);

            // Find operations
            var findTask = Task.Run(() => instance.FindData("1.0"));
            findTasks.Add(findTask);
        }

        Task.WaitAll(addTasks.Concat(findTasks).ToArray());

        // Assert
        // Due to data hash checking, duplicate data won't create new versions
        // So we expect at least some data was added (race condition may reduce count)
        Assert.True(instance.DataList.Count >= 1); // At least initial data exists
        Assert.True(instance.DataList.Count <= 16); // At most 1 initial + 5*3 added = 16
        
        // All find operations should return a valid result
        foreach (var findTask in findTasks)
        {
            var result = findTask.Result;
            Assert.NotNull(result);
            Assert.StartsWith("1.0.", result.Version);
        }
    }

    [Fact]
    public void SetSystemMetadata_ShouldSetRequiredSystemKeys()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        const bool isSync = true;
        const string callback = "https://callback.example.com";
        const string flowType = "MainFlow";

        // Act
        instance.SetInfoMetadata(isSync, callback, flowType);

        // Assert
        Assert.Equal("true", instance.ExtraProperties[DomainConsts.MetaDataKeys.Sync]);
        Assert.Equal(callback, instance.ExtraProperties[DomainConsts.MetaDataKeys.Callback]);
        Assert.Equal(flowType, instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType]);
    }

    [Fact]
    public void SetSystemMetadata_ShouldHandleNullCallback()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        const bool isSync = false;
        const string? callback = null;
        const string flowType = "SubFlow";

        // Act
        instance.SetInfoMetadata(isSync, callback, flowType);

        // Assert
        Assert.Equal("false", instance.ExtraProperties[DomainConsts.MetaDataKeys.Sync]);
        Assert.Equal(string.Empty, instance.ExtraProperties[DomainConsts.MetaDataKeys.Callback]);
        Assert.Equal(flowType, instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType]);
    }

    [Fact]
    public void SetSystemMetadata_ShouldMergeWithUserMetadata()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var userMetadata = new ExtraPropertyDictionary()
        {
            ["custom.key1"] = "value1",
            ["custom.key2"] = "value2"
        };
        const bool isSync = true;
        const string callback = "https://callback.example.com";
        const string flowType = "MainFlow";

        // Act
        instance.SetInfoMetadata(isSync, callback, flowType, userMetadata);

        // Assert
        // System metadata should be set
        Assert.Equal("true", instance.ExtraProperties[DomainConsts.MetaDataKeys.Sync]);
        Assert.Equal(callback, instance.ExtraProperties[DomainConsts.MetaDataKeys.Callback]);
        Assert.Equal(flowType, instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType]);
        
        // User metadata should be preserved
        Assert.Equal("value1", instance.ExtraProperties["custom.key1"]);
        Assert.Equal("value2", instance.ExtraProperties["custom.key2"]);
    }

    [Fact]
    public void SetSystemMetadata_ShouldNotOverrideExistingUserKeys()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var userMetadata = new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Sync] = "user-sync-value", // User tries to set system key
            ["custom.key"] = "custom-value"
        };
        const bool isSync = true;
        const string callback = "https://callback.example.com";
        const string flowType = "MainFlow";

        // Act
        instance.SetInfoMetadata(isSync, callback, flowType, userMetadata);

        // Assert
        // System should not override user-provided system keys due to TryAdd behavior
        Assert.Equal("user-sync-value", instance.ExtraProperties[DomainConsts.MetaDataKeys.Sync]);
        Assert.Equal(callback, instance.ExtraProperties[DomainConsts.MetaDataKeys.Callback]);
        Assert.Equal(flowType, instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType]);
        Assert.Equal("custom-value", instance.ExtraProperties["custom.key"]);
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void SetSystemMetadata_ShouldFormatSyncValueCorrectly(bool isSync, string expectedValue)
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        const string callback = "https://callback.example.com";
        const string flowType = "MainFlow";

        // Act
        instance.SetInfoMetadata(isSync, callback, flowType);

        // Assert
        Assert.Equal(expectedValue, instance.ExtraProperties[DomainConsts.MetaDataKeys.Sync]);
    }

    [Fact]
    public void Busy_ShouldSetStatusToBusy()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        Assert.Equal(InstanceStatus.Active, instance.Status);

        // Act
        instance.Busy();

        // Assert
        Assert.Equal(InstanceStatus.Busy, instance.Status);
        Assert.True(instance.IsBusy);
    }

    [Fact]
    public void Busy_ShouldNotChangeStatus_WhenInstanceIsCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.Complete("test-domain");
        Assert.True(instance.IsCompleted);

        // Act
        instance.Busy();

        // Assert
        Assert.Equal(InstanceStatus.Completed, instance.Status);
        Assert.False(instance.IsBusy);
    }

    [Fact]
    public void Active_ShouldSetStatusToActive()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.Busy();
        Assert.Equal(InstanceStatus.Busy, instance.Status);

        // Act
        instance.Active();

        // Assert
        Assert.Equal(InstanceStatus.Active, instance.Status);
        Assert.True(instance.IsActive);
    }

    [Fact]
    public void Active_ShouldNotChangeStatus_WhenInstanceIsCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.Complete("test-domain");
        Assert.True(instance.IsCompleted);

        // Act
        instance.Active();

        // Assert
        Assert.Equal(InstanceStatus.Completed, instance.Status);
        Assert.False(instance.IsActive);
    }

    [Fact]
    public void Fault_ShouldSetStatusToFaulted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.CreatedAt = DateTime.UtcNow.AddMinutes(-5);

        // Act
        instance.Fault("test-domain");

        // Assert
        Assert.Equal(InstanceStatus.Faulted, instance.Status);
        Assert.NotNull(instance.CompletedAt);
        Assert.Equal(instance.CompletedAt - instance.CreatedAt, instance.Duration);
        Assert.True(instance.IsCompleted);
    }

    [Fact]
    public void AddCorrelation_ShouldAddToChildCorrelations()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );

        // Act
        instance.AddCorrelation(correlation);

        // Assert
        Assert.Single(instance.ChildCorrelations);
        Assert.Contains(correlation, instance.ChildCorrelations);
    }

    [Fact]
    public void AddCorrelation_WithSubFlow_ShouldSetInstanceToBusy()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "S", // SubFlow
            "domain",
            "flow",
            null
        );

        // Act
        instance.AddCorrelation(correlation);

        // Assert
        Assert.Equal(InstanceStatus.Busy, instance.Status);
        Assert.True(instance.IsBusy);
    }

    [Fact]
    public void AddCorrelation_WithSubProcess_ShouldNotSetInstanceToBusy()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "P", // SubProcess
            "domain",
            "flow",
            null
        );

        // Act
        instance.AddCorrelation(correlation);

        // Assert
        Assert.Equal(InstanceStatus.Active, instance.Status);
        Assert.True(instance.IsActive);
    }

    [Fact]
    public void HasActiveSubFlow_ShouldReturnTrue_WhenActiveSubFlowExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );
        instance.AddCorrelation(correlation);

        // Act & Assert
        Assert.True(instance.HasActiveSubFlow);
    }

    [Fact]
    public void HasActiveSubFlow_ShouldReturnFalse_WhenSubFlowIsCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );
        correlation.Completed();
        instance.AddCorrelation(correlation);

        // Act & Assert
        Assert.False(instance.HasActiveSubFlow);
    }

    [Fact]
    public void HasActiveSubFlow_ShouldReturnFalse_WhenOnlySubProcessExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "P", // SubProcess
            "domain",
            "flow",
            null
        );
        instance.AddCorrelation(correlation);

        // Act & Assert
        Assert.False(instance.HasActiveSubFlow);
    }

    [Fact]
    public void Subflow_ShouldReturnActiveSubFlow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );
        instance.AddCorrelation(correlation);

        // Act
        var subflow = instance.Subflow;

        // Assert
        Assert.NotNull(subflow);
        Assert.Equal(correlation.Id, subflow.Id);
    }

    [Fact]
    public void Subflow_ShouldReturnNull_WhenNoActiveSubFlowExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var subflow = instance.Subflow;

        // Assert
        Assert.Null(subflow);
    }

    [Fact]
    public void IsSubFlow_ShouldReturnTrue_WhenFlowTypeIsSubFlow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "S";

        // Act & Assert
        Assert.True(instance.IsSubFlow);
    }

    [Fact]
    public void IsSubFlow_ShouldReturnFalse_WhenFlowTypeIsNotSubFlow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "F"; // Flow (Main Flow)

        // Act & Assert
        Assert.False(instance.IsSubFlow);
    }

    [Fact]
    public void IsSubItem_ShouldReturnTrue_ForSubFlow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "S";

        // Act & Assert
        Assert.True(instance.IsSubItem);
    }

    [Fact]
    public void IsSubItem_ShouldReturnTrue_ForSubProcess()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "P";

        // Act & Assert
        Assert.True(instance.IsSubItem);
    }

    [Fact]
    public void IsSubItem_ShouldReturnFalse_ForMainFlow()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "F"; // Flow (Main Flow)

        // Act & Assert
        Assert.False(instance.IsSubItem);
    }

    [Fact]
    public void ShouldPublishCompletionEvent_ShouldReturnTrue_WhenSubItemIsCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "S";
        instance.Complete("test-domain");

        // Act & Assert
        Assert.True(instance.ShouldPublishCompletionEvent());
    }

    [Fact]
    public void ShouldPublishCompletionEvent_ShouldReturnFalse_WhenSubItemIsNotCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "S";

        // Act & Assert
        Assert.False(instance.ShouldPublishCompletionEvent());
    }

    [Fact]
    public void ShouldPublishCompletionEvent_ShouldReturnFalse_WhenMainFlowIsCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.ExtraProperties[DomainConsts.MetaDataKeys.FlowType] = "F"; // Flow (Main Flow)
        instance.Complete("test-domain");

        // Act & Assert
        Assert.False(instance.ShouldPublishCompletionEvent());
    }

    [Fact]
    public void CreateSnapshot_ShouldCreateDeepCopy()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.SetKey("test-key");
        instance.AddTags(new[] { "tag1", "tag2" });
        instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"key\":\"value\"}"));
        
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            instance.Id,
            "parent-state",
            Guid.NewGuid(),
            "S",
            "domain",
            "flow",
            null
        );
        instance.AddCorrelation(correlation);

        // Act
        var snapshot = instance.CreateSnapshot();

        // Assert
        Assert.Equal(instance.Id, snapshot.Id);
        Assert.Equal(instance.Key, snapshot.Key);
        Assert.Equal(instance.Flow, snapshot.Flow);
        Assert.Equal(instance.Status, snapshot.Status);
        Assert.Equal(instance.CurrentState, snapshot.CurrentState);
        Assert.Equal(instance.Tags.Count, snapshot.Tags.Count);
        Assert.Equal(instance.DataList.Count, snapshot.DataList.Count);
        Assert.Equal(instance.ChildCorrelations.Count, snapshot.ChildCorrelations.Count);
    }

    [Fact]
    public void GetCurrentState_ShouldReturnEmptyString_WhenCurrentStateIsNull()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var currentState = instance.GetCurrentState;

        // Assert
        Assert.Equal(string.Empty, currentState);
    }

    [Fact]
    public void GetCurrentState_ShouldReturnCurrentState_WhenSet()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var state = StateFactory.CreateDefault();
        instance.ChangeState(state);

        // Act
        var currentState = instance.GetCurrentState;

        // Assert
        Assert.Equal("test-state", currentState);
    }

    [Fact]
    public void IsCompleted_ShouldReturnTrue_WhenStatusIsCompleted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.Complete("test-domain");

        // Act & Assert
        Assert.True(instance.IsCompleted);
    }

    [Fact]
    public void IsCompleted_ShouldReturnTrue_WhenStatusIsFaulted()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.Fault("test-domain");

        // Act & Assert
        Assert.True(instance.IsCompleted);
    }

    [Fact]
    public void IsCompleted_ShouldReturnFalse_WhenStatusIsActive()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act & Assert
        Assert.False(instance.IsCompleted);
    }

    [Fact]
    public void IsBusy_ShouldReturnTrue_WhenStatusIsBusy()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.Busy();

        // Act & Assert
        Assert.True(instance.IsBusy);
    }

    [Fact]
    public void IsActive_ShouldReturnTrue_WhenStatusIsActive()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act & Assert
        Assert.True(instance.IsActive);
    }

    [Fact]
    public void IsTransient_ShouldBeTrue_AfterCreation()
    {
        // Arrange & Act
        var instance = InstanceFactory.CreateDefault();

        // Assert
        Assert.True(instance.IsTransient);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Create_ShouldAllowNullOrEmptyKey(string? key)
    {
        // Arrange & Act
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", key);

        // Assert
        Assert.Equal(key, instance.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Create_ShouldThrow_WhenFlowIsInvalid(string? flow)
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentException>(() => Instance.Create(Guid.NewGuid(), flow!, "key"));
    }

    [Fact]
    public void ExtraProperties_ShouldBeInitializedEmpty()
    {
        // Arrange & Act
        var instance = InstanceFactory.CreateDefault();

        // Assert
        Assert.NotNull(instance.ExtraProperties);
        Assert.Empty(instance.ExtraProperties);
    }

    [Fact]
    public void Tags_ShouldBeInitializedEmpty()
    {
        // Arrange & Act
        var instance = InstanceFactory.CreateDefault();

        // Assert
        Assert.NotNull(instance.Tags);
        Assert.Empty(instance.Tags);
    }

    [Fact]
    public void DataList_ShouldBeInitializedEmpty()
    {
        // Arrange & Act
        var instance = InstanceFactory.CreateDefault();

        // Assert
        Assert.NotNull(instance.DataList);
        Assert.Empty(instance.DataList);
    }

    [Fact]
    public void ChildCorrelations_ShouldBeInitializedEmpty()
    {
        // Arrange & Act
        var instance = InstanceFactory.CreateDefault();

        // Assert
        Assert.NotNull(instance.ChildCorrelations);
        Assert.Empty(instance.ChildCorrelations);
    }

    [Fact]
    public void Data_ShouldReturnNull_WhenNoDataExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var data = instance.Data;

        // Assert
        Assert.Null(data);
    }

    [Fact]
    public void LatestData_ShouldReturnNull_WhenNoDataExists()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var latestData = instance.LatestData;

        // Assert
        Assert.Null(latestData);
    }

    [Fact]
    public void GetNextHistorySequence_ShouldReturnZero_ForFirstDataEntry()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var data = JsonData.CreateFrom("{\"key\":\"value1\"}");

        // Act
        var result = instance.AddDataWithVersion(Guid.NewGuid(), data, "1.0.0");

        // Assert
        Assert.Equal(0, result.HistorySequence);
    }

    [Fact]
    public void GetNextHistorySequence_ShouldReturnOne_ForSecondDataWithSameVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        var data1 = JsonData.CreateFrom("{\"key\":\"value1\"}");
        var data2 = JsonData.CreateFrom("{\"key\":\"value2\"}");

        // Act
        var result1 = instance.AddDataWithVersion(Guid.NewGuid(), data1, "1.0.0");
        var result2 = instance.AddDataWithVersion(Guid.NewGuid(), data2, "1.0.0");

        // Assert
        Assert.Equal(0, result1.HistorySequence);
        Assert.Equal(1, result2.HistorySequence);
    }

    [Fact]
    public void GetNextHistorySequence_ShouldIncrementSequentially_ForMultipleDataWithSameVersion()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act
        var result0 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");
        var result1 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0");
        var result2 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0");
        var result3 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "1.0.0");

        // Assert
        Assert.Equal(0, result0.HistorySequence);
        Assert.Equal(1, result1.HistorySequence);
        Assert.Equal(2, result2.HistorySequence);
        Assert.Equal(3, result3.HistorySequence);
    }

    [Fact]
    public void GetNextHistorySequence_ShouldMaintainSeparateSequences_ForDifferentVersions()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act - Add data for version 1.0.0
        var v1_0 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");
        var v1_1 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0");

        // Add data for version 2.0.0
        var v2_0 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "2.0.0");
        var v2_1 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "2.0.0");

        // Add more data for version 1.0.0
        var v1_2 = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":5}"), "1.0.0");

        // Assert - Each version should have its own sequence counter
        Assert.Equal(0, v1_0.HistorySequence);
        Assert.Equal(1, v1_1.HistorySequence);
        Assert.Equal(2, v1_2.HistorySequence);
        
        Assert.Equal(0, v2_0.HistorySequence);
        Assert.Equal(1, v2_1.HistorySequence);
    }

    [Fact]
    public void GetNextHistorySequence_ShouldWorkCorrectly_WhenUsedViaAddData()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();

        // Act - AddData internally uses GetNextHistorySequence when creating new versions
        var result1 = instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"));
        var result2 = instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"));
        var result3 = instance.AddData(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"));

        // Assert
        Assert.Equal(0, result1.HistorySequence);
        Assert.Equal(0, result2.HistorySequence); // New version, so resets to 0
        Assert.Equal(0, result3.HistorySequence); // New version, so resets to 0
    }

    [Fact]
    public void GetVersionHistory_ShouldReturnAllEntriesForVersion_OrderedBySequence()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "2.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":4}"), "1.0.0");

        // Act
        var history = instance.GetVersionHistory("1.0.0").ToList();

        // Assert
        Assert.Equal(3, history.Count);
        Assert.Equal(0, history[0].HistorySequence);
        Assert.Equal(1, history[1].HistorySequence);
        Assert.Equal(2, history[2].HistorySequence);
    }

    [Fact]
    public void GetLatestDataForVersion_ShouldReturnHighestSequence()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":2}"), "1.0.0");
        var latest = instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":3}"), "1.0.0");

        // Act
        var result = instance.GetLatestDataForVersion("1.0.0");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(latest.Id, result.Id);
        Assert.Equal(2, result.HistorySequence);
    }

    [Fact]
    public void GetLatestDataForVersion_ShouldReturnNull_WhenVersionNotFound()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");

        // Act
        var result = instance.GetLatestDataForVersion("2.0.0");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void GetVersionHistory_ShouldReturnEmptyList_WhenVersionNotFound()
    {
        // Arrange
        var instance = InstanceFactory.CreateDefault();
        instance.AddDataWithVersion(Guid.NewGuid(), JsonData.CreateFrom("{\"v\":1}"), "1.0.0");

        // Act
        var history = instance.GetVersionHistory("2.0.0").ToList();

        // Assert
        Assert.Empty(history);
    }
}