using System;
using System.Text.Json;
using Xunit;

namespace BBT.Workflow.Definitions;

public class GetInstancesTaskTests
{
    [Fact]
    public void Create_ShouldInitializePropertiesFromConfig()
    {
        // Arrange
        var config = """
            {
                "key": "get-instances",
                "type": "15",
                "domain": "banking",
                "flow": "customer-onboarding",
                "page": 2,
                "pageSize": 25,
                "sort": "-CreatedAt",
                "filter": ["status:active", "type:premium"],
                "useDapr": true
            }
            """;

        // Act
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Assert
        Assert.NotNull(task);
        Assert.Equal("banking", task.TriggerDomain);
        Assert.Equal("customer-onboarding", task.TriggerFlow);
        Assert.Equal(2, task.Page);
        Assert.Equal(25, task.PageSize);
        Assert.Equal("-CreatedAt", task.Sort);
        Assert.NotNull(task.Filter);
        Assert.Equal(2, task.Filter.Length);
        Assert.Contains("status:active", task.Filter);
        Assert.Contains("type:premium", task.Filter);
        Assert.True(task.UseDapr);
    }

    [Fact]
    public void Create_ShouldUseDefaultValuesWhenNotProvided()
    {
        // Arrange
        var config = """
            {
                "key": "get-instances",
                "type": "15",
                "domain": "banking",
                "flow": "customer-onboarding"
            }
            """;

        // Act
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Assert
        Assert.NotNull(task);
        Assert.Equal(1, task.Page);
        Assert.Equal(10, task.PageSize);
        Assert.Null(task.Sort);
        Assert.Null(task.Filter);
        Assert.False(task.UseDapr);
    }

    [Theory]
    [InlineData(0, 1)]   // Invalid page defaults to 1
    [InlineData(-5, 1)]  // Negative page defaults to 1
    [InlineData(5, 5)]   // Valid page preserved
    public void Create_ShouldHandlePageValidation(int inputPage, int expectedPage)
    {
        // Arrange
        var config = $@"{{
            ""key"": ""test"",
            ""type"": ""15"",
            ""domain"": ""test"",
            ""flow"": ""test"",
            ""page"": {inputPage}
        }}";

        // Act
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Assert
        Assert.Equal(expectedPage, task.Page);
    }

    [Theory]
    [InlineData(0, 10)]   // Invalid pageSize defaults to 10
    [InlineData(-5, 10)]  // Negative pageSize defaults to 10
    [InlineData(50, 50)]  // Valid pageSize preserved
    public void Create_ShouldHandlePageSizeValidation(int inputPageSize, int expectedPageSize)
    {
        // Arrange
        var config = $@"{{
            ""key"": ""test"",
            ""type"": ""15"",
            ""domain"": ""test"",
            ""flow"": ""test"",
            ""pageSize"": {inputPageSize}
        }}";

        // Act
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Assert
        Assert.Equal(expectedPageSize, task.PageSize);
    }

    [Fact]
    public void Clone_ShouldCreateDeepCopy()
    {
        // Arrange
        var config = """
            {
                "key": "original",
                "type": "15",
                "domain": "banking",
                "flow": "customer-onboarding",
                "page": 3,
                "pageSize": 50,
                "sort": "Name",
                "filter": ["status:active"],
                "useDapr": true
            }
            """;
        var original = GetInstancesTask.Create(config.ToJsonElement());
        original.SetReference(new Reference("original-key", "banking", "sys-tasks", "1.0.0"));

        // Act
        var cloned = original.Clone() as GetInstancesTask;

        // Assert
        Assert.NotNull(cloned);
        Assert.NotSame(original, cloned);
        Assert.Equal(original.Key, cloned.Key);
        Assert.Equal(original.TriggerDomain, cloned.TriggerDomain);
        Assert.Equal(original.TriggerFlow, cloned.TriggerFlow);
        Assert.Equal(original.Page, cloned.Page);
        Assert.Equal(original.PageSize, cloned.PageSize);
        Assert.Equal(original.Sort, cloned.Sort);
        Assert.Equal(original.Filter, cloned.Filter);
        Assert.Equal(original.UseDapr, cloned.UseDapr);
    }

    [Fact]
    public void CloneTyped_ShouldReturnTypedCopy()
    {
        // Arrange
        var config = """
            {
                "key": "original",
                "type": "15",
                "domain": "test",
                "flow": "test"
            }
            """;
        var original = GetInstancesTask.Create(config.ToJsonElement());
        original.SetReference(new Reference("original-key", "test", "sys-tasks", "1.0.0"));

        // Act
        var cloned = original.CloneTyped();

        // Assert
        Assert.NotNull(cloned);
        Assert.IsType<GetInstancesTask>(cloned);
        Assert.NotSame(original, cloned);
    }

    [Fact]
    public void Reset_ShouldClearAllProperties()
    {
        // Arrange
        var config = """
            {
                "key": "test",
                "type": "15",
                "domain": "banking",
                "flow": "customer",
                "page": 5,
                "pageSize": 100,
                "sort": "-CreatedAt",
                "filter": ["status:active"],
                "useDapr": true
            }
            """;
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Act
        task.Reset();

        // Assert
        Assert.Equal(string.Empty, task.TriggerDomain);
        Assert.Equal(string.Empty, task.TriggerFlow);
        Assert.Equal(1, task.Page);
        Assert.Equal(10, task.PageSize);
        Assert.Null(task.Sort);
        Assert.Null(task.Filter);
        Assert.False(task.UseDapr);
    }

    [Fact]
    public void SetDomain_ShouldUpdateDomain()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetDomain("new-domain");

        // Assert
        Assert.Equal("new-domain", task.TriggerDomain);
    }

    [Fact]
    public void SetDomain_ShouldThrowForNullOrEmpty()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act & Assert - ArgumentNullException for null, ArgumentException for empty/whitespace
        Assert.ThrowsAny<ArgumentException>(() => task.SetDomain(null!));
        Assert.ThrowsAny<ArgumentException>(() => task.SetDomain(""));
        Assert.ThrowsAny<ArgumentException>(() => task.SetDomain("   "));
    }

    [Fact]
    public void SetFlow_ShouldUpdateFlow()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetFlow("new-flow");

        // Assert
        Assert.Equal("new-flow", task.TriggerFlow);
    }

    [Fact]
    public void SetFlow_ShouldThrowForNullOrEmpty()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act & Assert - ArgumentNullException for null, ArgumentException for empty/whitespace
        Assert.ThrowsAny<ArgumentException>(() => task.SetFlow(null!));
        Assert.ThrowsAny<ArgumentException>(() => task.SetFlow(""));
        Assert.ThrowsAny<ArgumentException>(() => task.SetFlow("   "));
    }

    [Fact]
    public void SetPage_ShouldUpdatePage()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetPage(5);

        // Assert
        Assert.Equal(5, task.Page);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(10, 10)]
    public void SetPage_ShouldDefaultToOneForInvalidValues(int input, int expected)
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetPage(input);

        // Assert
        Assert.Equal(expected, task.Page);
    }

    [Fact]
    public void SetPageSize_ShouldUpdatePageSize()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetPageSize(50);

        // Assert
        Assert.Equal(50, task.PageSize);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(25, 25)]
    public void SetPageSize_ShouldDefaultToTenForInvalidValues(int input, int expected)
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetPageSize(input);

        // Assert
        Assert.Equal(expected, task.PageSize);
    }

    [Fact]
    public void SetSort_ShouldUpdateSort()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetSort("-CreatedAt");

        // Assert
        Assert.Equal("-CreatedAt", task.Sort);
    }

    [Fact]
    public void SetSort_ShouldAcceptNull()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();
        task.SetSort("Name");

        // Act
        task.SetSort(null);

        // Assert
        Assert.Null(task.Sort);
    }

    [Fact]
    public void SetFilter_ShouldUpdateFilter()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();
        var filters = new[] { "status:active", "type:premium" };

        // Act
        task.SetFilter(filters);

        // Assert
        Assert.NotNull(task.Filter);
        Assert.Equal(2, task.Filter.Length);
        Assert.Equal(filters, task.Filter);
    }

    [Fact]
    public void SetFilter_ShouldAcceptNull()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();
        task.SetFilter(new[] { "test" });

        // Act
        task.SetFilter(null);

        // Assert
        Assert.Null(task.Filter);
    }

    [Fact]
    public void SetUseDapr_ShouldUpdateUseDapr()
    {
        // Arrange
        var task = GetInstancesTask.CreateEmpty();

        // Act
        task.SetUseDapr(true);

        // Assert
        Assert.True(task.UseDapr);
    }

    [Fact]
    public void CopyFromInternal_ShouldCopyAllProperties()
    {
        // Arrange
        var config = """
            {
                "key": "source",
                "type": "15",
                "domain": "banking",
                "flow": "customer",
                "page": 3,
                "pageSize": 50,
                "sort": "Name",
                "filter": ["status:active"],
                "useDapr": true
            }
            """;
        var source = GetInstancesTask.Create(config.ToJsonElement());
        var target = GetInstancesTask.CreateEmpty();

        // Act
        target.CopyFromInternal(source);

        // Assert
        Assert.Equal(source.TriggerDomain, target.TriggerDomain);
        Assert.Equal(source.TriggerFlow, target.TriggerFlow);
        Assert.Equal(source.Page, target.Page);
        Assert.Equal(source.PageSize, target.PageSize);
        Assert.Equal(source.Sort, target.Sort);
        Assert.Equal(source.Filter, target.Filter);
        Assert.Equal(source.UseDapr, target.UseDapr);
    }

    [Fact]
    public void GetTaskType_ShouldReturnGetInstances()
    {
        // Arrange
        var config = """
            {
                "key": "test",
                "type": "15",
                "domain": "test",
                "flow": "test"
            }
            """;
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Act
        var taskType = task.GetTaskType();

        // Assert
        Assert.Equal(TaskType.GetInstances, taskType);
    }

    [Fact]
    public void Create_ShouldHandleEmptyFilterArray()
    {
        // Arrange
        var config = """
            {
                "key": "test",
                "type": "15",
                "domain": "test",
                "flow": "test",
                "filter": []
            }
            """;

        // Act
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Assert
        Assert.Null(task.Filter);
    }

    [Fact]
    public void Create_ShouldFilterOutEmptyStringsFromFilterArray()
    {
        // Arrange
        var config = """
            {
                "key": "test",
                "type": "15",
                "domain": "test",
                "flow": "test",
                "filter": ["status:active", "", "  ", "type:premium"]
            }
            """;

        // Act
        var task = GetInstancesTask.Create(config.ToJsonElement());

        // Assert
        Assert.NotNull(task.Filter);
        Assert.Equal(2, task.Filter.Length);
        Assert.Contains("status:active", task.Filter);
        Assert.Contains("type:premium", task.Filter);
    }

    [Fact]
    public void SetReference_ShouldSetKeyDomainAndVersion()
    {
        // Arrange
        var config = """
            {
                "type": "15",
                "domain": "test",
                "flow": "test"
            }
            """;
        var task = GetInstancesTask.Create(config.ToJsonElement());
        var reference = new Reference("test-key", "test-domain", "sys-tasks", "1.0.0");

        // Act
        task.SetReference(reference);

        // Assert
        Assert.Equal("test-key", task.Key);
        Assert.Equal("test-domain", task.Domain);
        Assert.Equal("1.0.0", task.Version);
    }
}
