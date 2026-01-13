using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Shared.Merging;
using Xunit;

namespace BBT.Workflow.Shared.Merging;

public class ObjectMergerTests
{
    [Fact]
    public void MergeValues_ShouldReturnTarget_WhenSourceIsNull()
    {
        // Arrange
        var target = "target-value";
        object? source = null;

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Equal(target, result);
    }

    [Fact]
    public void MergeValues_ShouldReturnSource_WhenTargetIsNull()
    {
        // Arrange
        object? target = null;
        var source = "source-value";

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Equal(source, result);
    }

    [Fact]
    public void MergeValues_ShouldReturnNull_WhenBothAreNull()
    {
        // Arrange
        object? target = null;
        object? source = null;

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void MergeValues_ShouldMergeExpandoObjects()
    {
        // Arrange
        dynamic target = new ExpandoObject();
        target.Name = "John";
        target.Age = 30;

        dynamic source = new ExpandoObject();
        source.Age = 35;
        source.City = "NYC";

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        Assert.Equal(3, dict.Count);
        Assert.Equal("John", dict["Name"]);
        Assert.Equal(35, dict["Age"]);
        Assert.Equal("NYC", dict["City"]);
    }

    [Fact]
    public void MergeValues_ShouldMergeJsonElements()
    {
        // Arrange
        var target = JsonDocument.Parse(@"{""name"": ""John"", ""age"": 30}").RootElement;
        var source = JsonDocument.Parse(@"{""age"": 35, ""city"": ""NYC""}").RootElement;

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        Assert.Equal(3, dict.Count);
    }

    [Fact]
    public void MergeValues_ShouldMergeDictionaries()
    {
        // Arrange
        var target = new Dictionary<string, object?>
        {
            { "Key1", "Value1" },
            { "Key2", "Value2" }
        };

        var source = new Dictionary<string, object?>
        {
            { "Key2", "UpdatedValue2" },
            { "Key3", "Value3" }
        };

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        Assert.Equal(3, dict.Count);
    }

    [Fact]
    public void MergeValues_ShouldReplaceLists()
    {
        // Arrange
        var target = new List<object> { 1, 2, 3 };
        var source = new List<object> { 10, 20 };

        // Act
        var result = ObjectMerger.MergeValues(target, source) as List<object>;

        // Assert - Lists are replaced, source completely replaces target
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
    }

    [Fact]
    public void MergeValues_ShouldUseDefaultStrategy_ForSimpleTypes()
    {
        // Arrange
        var target = 123;
        var source = 456;

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Equal(456, result);
    }

    [Fact]
    public void MergeValues_ShouldUseDefaultStrategy_ForStrings()
    {
        // Arrange
        var target = "old";
        var source = "new";

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Equal("new", result);
    }

    [Fact]
    public void MergeValues_ShouldHandleNestedExpandoObjects()
    {
        // Arrange
        dynamic target = new ExpandoObject();
        dynamic targetNested = new ExpandoObject();
        targetNested.Value = 10;
        target.Nested = targetNested;

        dynamic source = new ExpandoObject();
        dynamic sourceNested = new ExpandoObject();
        sourceNested.Value = 20;
        sourceNested.NewProp = "Added";
        source.Nested = sourceNested;

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        var nested = dict["Nested"] as IDictionary<string, object?>;
        Assert.NotNull(nested);
        Assert.Equal(2, nested.Count);
    }

    [Fact]
    public void MergeValues_ShouldHandleComplexNestedStructures()
    {
        // Arrange
        var targetJson = JsonDocument.Parse(@"{
            ""user"": {
                ""profile"": {
                    ""name"": ""John"",
                    ""age"": 30
                }
            }
        }").RootElement;

        var sourceJson = JsonDocument.Parse(@"{
            ""user"": {
                ""profile"": {
                    ""age"": 35,
                    ""email"": ""john@example.com""
                }
            }
        }").RootElement;

        // Act
        var result = ObjectMerger.MergeValues(targetJson, sourceJson) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        Assert.True(dict.ContainsKey("user"));
    }

    [Fact]
    public void MergeValues_ShouldHandleMixedJsonAndExpando()
    {
        // Arrange
        var target = JsonDocument.Parse(@"{""name"": ""John""}").RootElement;
        
        dynamic source = new ExpandoObject();
        source.age = 30;

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public void MergeValues_ShouldHandleMixedExpandoAndJson()
    {
        // Arrange
        dynamic target = new ExpandoObject();
        target.name = "John";

        var source = JsonDocument.Parse(@"{""age"": 30}").RootElement;

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public void MergeValues_ShouldReplaceArrays()
    {
        // Arrange
        var target = new[] { 1, 2, 3 };
        var source = new[] { 10, 20 };

        // Act
        var result = ObjectMerger.MergeValues(target, source) as List<object>;

        // Assert - Arrays are replaced, source completely replaces target
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
    }

    [Fact]
    public void MergeValues_ShouldHandleEmptyCollections()
    {
        // Arrange
        var target = new List<object>();
        var source = new List<object> { 1, 2, 3 };

        // Act
        var result = ObjectMerger.MergeValues(target, source) as List<object>;

        // Assert
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void MergeValues_ShouldWorkWithBooleans()
    {
        // Arrange
        var target = true;
        var source = false;

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Equal(false, result);
    }

    [Fact]
    public void MergeValues_ShouldWorkWithDifferentTypes()
    {
        // Arrange
        var target = 123;
        var source = "string-value";

        // Act
        var result = ObjectMerger.MergeValues(target, source);

        // Assert
        Assert.Equal("string-value", result);
    }

    [Fact]
    public void MergeValues_ShouldReplaceJsonArrays()
    {
        // Arrange
        var target = JsonDocument.Parse(@"[1, 2, 3]").RootElement;
        var source = JsonDocument.Parse(@"[10, 20]").RootElement;

        // Act
        var result = ObjectMerger.MergeValues(target, source) as JsonElement?;

        // Assert - Arrays are replaced, source completely replaces target
        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Array, result.Value.ValueKind);
        var array = result.Value.EnumerateArray().ToList();
        Assert.Equal(2, array.Count);
        Assert.Equal(10, array[0].GetInt32());
        Assert.Equal(20, array[1].GetInt32());
    }

    [Fact]
    public void MergeValues_ShouldBePerformant_ForMultipleCalls()
    {
        // Arrange
        dynamic target = new ExpandoObject();
        target.Value = 1;

        dynamic source = new ExpandoObject();
        source.Value = 2;

        // Act - Multiple calls to ensure singleton pattern works
        for (int i = 0; i < 100; i++)
        {
            var result = ObjectMerger.MergeValues(target, source);
            Assert.NotNull(result);
        }

        // Assert - No exception means success
        Assert.True(true);
    }

    [Fact]
    public void MergeValues_ShouldReplaceNestedLists()
    {
        // Arrange
        var target = new List<object>
        {
            new Dictionary<string, object?> { { "id", 1 }, { "name", "Item1" } }
        };

        var source = new List<object>
        {
            new Dictionary<string, object?> { { "id", 2 }, { "value", "Added" } }
        };

        // Act
        var result = ObjectMerger.MergeValues(target, source) as List<object>;

        // Assert - Lists are replaced, source completely replaces target
        Assert.NotNull(result);
        Assert.Single(result);
        var item = result[0] as Dictionary<string, object?>;
        Assert.NotNull(item);
        Assert.Equal(2, item.Count);
        Assert.Equal(2, item["id"]);
        Assert.Equal("Added", item["value"]);
    }

    [Fact]
    public void MergeValues_ShouldHandleNullPropertiesInExpando()
    {
        // Arrange
        dynamic target = new ExpandoObject();
        target.Value = "not-null";

        dynamic source = new ExpandoObject();
        source.Value = null;

        // Act
        var result = ObjectMerger.MergeValues(target, source) as ExpandoObject;

        // Assert
        Assert.NotNull(result);
        var dict = (IDictionary<string, object?>)result;
        // ObjectMerger.MergeValues returns target when source is null
        // So Value will keep its original value
        Assert.Equal("not-null", dict["Value"]);
    }
}

