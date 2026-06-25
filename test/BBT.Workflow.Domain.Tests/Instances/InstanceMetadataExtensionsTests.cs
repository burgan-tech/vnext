using System;
using BBT.Aether;
using Xunit;

namespace BBT.Workflow.Instances;

public class InstanceMetadataExtensionsTests : DomainTestBase<DomainEntryPoint>
{
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
}
