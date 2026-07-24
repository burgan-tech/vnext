using System;
using BBT.Aether;
using Xunit;

namespace BBT.Workflow.Instances;

public class InstanceMetadataExtensionsTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void TrackResourceLock_RecordsKey_AndGetReturnsIt()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

        instance.TrackResourceLock("limit:scope:2026-07-24");

        Assert.Equal(new[] { "limit:scope:2026-07-24" }, instance.GetTrackedResourceLocks());
    }

    [Fact]
    public void TrackResourceLock_IsIdempotent_AndPreservesOrderForDistinctKeys()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

        instance.TrackResourceLock("a");
        instance.TrackResourceLock("b");
        instance.TrackResourceLock("a"); // duplicate ignored

        Assert.Equal(new[] { "a", "b" }, instance.GetTrackedResourceLocks());
    }

    [Fact]
    public void TrackResourceLock_IgnoresNullOrWhitespaceKeys()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

        instance.TrackResourceLock("   ");

        Assert.Empty(instance.GetTrackedResourceLocks());
    }

    [Fact]
    public void GetTrackedResourceLocks_WhenNothingTracked_ReturnsEmpty()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

        Assert.Empty(instance.GetTrackedResourceLocks());
    }

    [Fact]
    public void GetTrackedResourceLocks_WhenMetadataMalformed_ReturnsEmpty()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.ResourceLocks] = "not-a-json-array"
        });

        Assert.Empty(instance.GetTrackedResourceLocks());
    }

    [Fact]
    public void GetRootInstanceId_WhenNoRootKeyInExtraProperties_ReturnsSelfId()
    {
        // Arrange — root instance (A): no parent, no root.instance.id
        var id = Guid.NewGuid();
        var instance = Instance.Create(id, "flow-a", "1.0.0", "key-a");

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(id, result);
    }

    [Fact]
    public void GetRootInstanceId_WhenRootKeyPresent_ReturnsStoredRootId()
    {
        // Arrange — subflow instance (C): has root.instance.id = A's ID
        var instanceId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow-c", "1.0.0", "key-c");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.RootInstanceId] = rootId
        });

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(rootId, result);
    }

    [Fact]
    public void GetRootInstanceId_WhenRootKeyStoredAsString_ParsesAndReturnsRootId()
    {
        // Arrange — ExtraPropertyDictionary may round-trip values as strings after deserialization
        var instanceId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow-b", "1.0.0", "key-b");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.RootInstanceId] = rootId.ToString()
        });

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(rootId, result);
    }

    [Fact]
    public void GetRootInstanceId_WhenRootKeyIsGuidEmpty_ReturnsSelfId()
    {
        // Arrange — stored value is Guid.Empty (defensive guard)
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "flow-d", "1.0.0", "key-d");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.RootInstanceId] = Guid.Empty
        });

        // Act
        var result = instance.GetRootInstanceId();

        // Assert
        Assert.Equal(instanceId, result);
    }
}
