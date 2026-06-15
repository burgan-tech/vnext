using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Xunit;
using Shouldly;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// Unit tests for Instance column filtering functionality
/// Tests InstanceFieldDiscriminator and InstanceColumnConditionBuilder
/// </summary>
public class InstanceColumnFilterTests
{
    #region InstanceFieldDiscriminator Tests

    [Theory]
    [InlineData("Key", true)]
    [InlineData("key", true)]
    [InlineData("Status", true)]
    [InlineData("status", true)]
    [InlineData("Flow", true)]
    [InlineData("flow", true)]
    [InlineData("CurrentState", true)]
    [InlineData("State", true)] // Alias
    [InlineData("CreatedAt", true)]
    [InlineData("createdat", true)]
    [InlineData("ModifiedAt", true)]
    [InlineData("CompletedAt", true)]
    [InlineData("IsTransient", true)]
    [InlineData("CreatedBy", true)]
    [InlineData("createdby", true)]
    [InlineData("CreatedByBehalfOf", true)]
    [InlineData("ModifiedBy", true)]
    [InlineData("ModifiedByBehalfOf", true)]
    [InlineData("attributes", false)]
    [InlineData("triggerId", false)]
    [InlineData("customField", false)]
    [InlineData("", false)]
    public void IsInstanceColumn_ShouldIdentifyColumnCorrectly(string fieldName, bool expectedResult)
    {
        // Act
        var result = InstanceFieldDiscriminator.IsInstanceColumn(fieldName);

        // Assert
        result.ShouldBe(expectedResult);
    }

    [Theory]
    [InlineData("Key", "Key")]
    [InlineData("key", "Key")]
    [InlineData("Status", "Status")]
    [InlineData("status", "Status")]
    [InlineData("State", "EffectiveState")] // Alias mapping
    [InlineData("state", "EffectiveState")]
    [InlineData("CreatedAt", "CreatedAt")]
    [InlineData("createdat", "CreatedAt")]
    [InlineData("CreatedBy", "CreatedBy")]
    [InlineData("createdby", "CreatedBy")]
    [InlineData("CreatedByBehalfOf", "CreatedByBehalfOf")]
    [InlineData("ModifiedBy", "ModifiedBy")]
    [InlineData("ModifiedByBehalfOf", "ModifiedByBehalfOf")]
    public void GetInstanceColumnName_ShouldReturnProperCaseName(string input, string expected)
    {
        // Act
        var result = InstanceFieldDiscriminator.GetInstanceColumnName(input);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void GetInstanceColumnName_ShouldThrowForInvalidColumn()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => 
            InstanceFieldDiscriminator.GetInstanceColumnName("invalidColumn"));
    }

    [Fact]
    public void SeparateFilters_ShouldSeparateInstanceAndJsonFilters()
    {
        // Arrange
        var filters = new[]
        {
            "key=eq:1111",
            "status=eq:A",
            "attributes=triggerId=eq:82111090771",
            "createdAt=gt:2024-01-01"
        };

        // Act
        var (instanceFilters, jsonFilters) = InstanceFieldDiscriminator.SeparateFilters(filters);

        // Assert
        instanceFilters.Length.ShouldBe(3);
        instanceFilters.ShouldContain("key=eq:1111");
        instanceFilters.ShouldContain("status=eq:A");
        instanceFilters.ShouldContain("createdAt=gt:2024-01-01");

        jsonFilters.Length.ShouldBe(1);
        jsonFilters.ShouldContain("attributes=triggerId=eq:82111090771");
    }

    [Fact]
    public void SeparateFilters_ShouldHandleEmptyArray()
    {
        // Arrange
        var filters = Array.Empty<string>();

        // Act
        var (instanceFilters, jsonFilters) = InstanceFieldDiscriminator.SeparateFilters(filters);

        // Assert
        instanceFilters.ShouldBeEmpty();
        jsonFilters.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Active", "A")]
    [InlineData("active", "A")]
    [InlineData("Busy", "B")]
    [InlineData("Completed", "C")]
    [InlineData("Faulted", "F")]
    [InlineData("Passive", "P")]
    [InlineData("A", "A")] // Direct code
    [InlineData("B", "B")]
    public void ResolveStatusValue_ShouldMapStatusNamesToCodes(string input, string expected)
    {
        // Act
        var result = InstanceFieldDiscriminator.ResolveStatusValue(input);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void ResolveStatusValues_ShouldMapMultipleValues()
    {
        // Arrange
        var values = new[] { "Active", "Busy", "A" };

        // Act
        var result = InstanceFieldDiscriminator.ResolveStatusValues(values);

        // Assert
        result.Length.ShouldBe(3);
        result[0].ShouldBe("A");
        result[1].ShouldBe("B");
        result[2].ShouldBe("A");
    }

    #endregion

    #region InstanceColumnConditionBuilder Tests

    [Fact]
    public void BuildCondition_Equals_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Key", "eq", "1111", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Key\" = {0}");
        parameters.Count.ShouldBe(1);
        parameters[0].Value.ShouldBe("1111");
        parameterIndex.ShouldBe(1);
    }

    [Fact]
    public void BuildCondition_NotEquals_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Status", "ne", "Active", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Status\" != {0}");
        parameters.Count.ShouldBe(1);
        parameters[0].Value.ShouldBe("A"); // Should be resolved to code
        parameterIndex.ShouldBe(1);
    }

    [Fact]
    public void BuildCondition_GreaterThan_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "CreatedAt", "gt", "2024-01-01", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"CreatedAt\" > {0}");
        parameters.Count.ShouldBe(1);
        parameterIndex.ShouldBe(1);
    }

    [Fact]
    public void BuildCondition_Between_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "CreatedAt", "between", "2024-01-01,2024-12-31", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"CreatedAt\" BETWEEN {0} AND {1}");
        parameters.Count.ShouldBe(2);
        parameterIndex.ShouldBe(2);
    }

    [Fact]
    public void BuildCondition_Like_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Key", "like", "test", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Key\" ILIKE {0}");
        parameters.Count.ShouldBe(1);
        parameters[0].Value.ShouldBe("%test%");
        parameterIndex.ShouldBe(1);
    }

    [Fact]
    public void BuildCondition_StartsWith_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Flow", "startswith", "workflow", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Flow\" ILIKE {0}");
        parameters.Count.ShouldBe(1);
        parameters[0].Value.ShouldBe("workflow%");
        parameterIndex.ShouldBe(1);
    }

    [Fact]
    public void BuildCondition_EndsWith_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Flow", "endswith", "flow", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Flow\" ILIKE {0}");
        parameters.Count.ShouldBe(1);
        parameters[0].Value.ShouldBe("%flow");
        parameterIndex.ShouldBe(1);
    }

    [Fact]
    public void BuildCondition_In_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Status", "in", "Active,Busy,Completed", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Status\" IN ({0}, {1}, {2})");
        parameters.Count.ShouldBe(3);
        parameters[0].Value.ShouldBe("A"); // Resolved
        parameters[1].Value.ShouldBe("B"); // Resolved
        parameters[2].Value.ShouldBe("C"); // Resolved
        parameterIndex.ShouldBe(3);
    }

    [Fact]
    public void BuildCondition_NotIn_ShouldBuildCorrectCondition()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Key", "nin", "test1,test2", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Key\" NOT IN ({0}, {1})");
        parameters.Count.ShouldBe(2);
        parameters[0].Value.ShouldBe("test1");
        parameters[1].Value.ShouldBe("test2");
        parameterIndex.ShouldBe(2);
    }

    [Fact]
    public void BuildCondition_Status_ShouldResolveStatusNames()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Status", "eq", "Active", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Status\" = {0}");
        parameters.Count.ShouldBe(1);
        parameters[0].Value.ShouldBe("A"); // Should be resolved from "Active" to "A"
    }

    [Fact]
    public void BuildCondition_InvalidColumn_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "InvalidColumn", "eq", "value", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_InvalidOperator_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "Key", "invalidop", "value", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_ComparisonOnBoolean_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "IsTransient", "gt", "true", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_BetweenOnBoolean_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "IsTransient", "between", "true,false", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_InvalidBetweenFormat_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "CreatedAt", "between", "2024-01-01", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_InvalidDateTimeValue_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "CreatedAt", "eq", "invalid-date", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_InvalidBooleanValue_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "IsTransient", "eq", "not-a-boolean", ref parameterIndex));
    }

    [Fact]
    public void BuildCondition_Status_InvalidOperator_ShouldThrow()
    {
        // Arrange
        var parameterIndex = 0;

        // Act & Assert - Status only supports eq, ne, in, nin
        Should.Throw<ArgumentException>(() =>
            InstanceColumnConditionBuilder.BuildCondition(
                "Status", "gt", "Active", ref parameterIndex));
    }

    [Theory]
    [InlineData("Key", "eq", "test-key")]
    [InlineData("Flow", "like", "workflow")]
    [InlineData("CurrentState", "startswith", "state")]
    [InlineData("Status", "in", "Active,Busy")]
    [InlineData("CreatedAt", "between", "2024-01-01,2024-12-31")]
    [InlineData("ModifiedAt", "gt", "2024-06-01")]
    [InlineData("CompletedAt", "le", "2024-12-31")]
    [InlineData("IsTransient", "eq", "true")]
    public void BuildCondition_AllColumns_ShouldWorkCorrectly(string column, string op, string value)
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            column, op, value, ref parameterIndex);

        // Assert
        condition.ShouldNotBeNullOrEmpty();
        parameters.ShouldNotBeEmpty();
    }

    #endregion

    #region Integration Tests

    [Fact]
    public void MixedFilters_ShouldBeSeparatedCorrectly()
    {
        // Arrange - Mix of Instance and JSON filters
        var filters = new[]
        {
            "key=eq:workflow-123",
            "status=eq:Active",
            "attributes=customerId=eq:12345",
            "createdAt=gt:2024-01-01",
            "attributes=amount=gt:1000",
            "flow=like:payment"
        };

        // Act
        var (instanceFilters, jsonFilters) = InstanceFieldDiscriminator.SeparateFilters(filters);

        // Assert
        instanceFilters.Length.ShouldBe(4);
        instanceFilters.ShouldContain("key=eq:workflow-123");
        instanceFilters.ShouldContain("status=eq:Active");
        instanceFilters.ShouldContain("createdAt=gt:2024-01-01");
        instanceFilters.ShouldContain("flow=like:payment");

        jsonFilters.Length.ShouldBe(2);
        jsonFilters.ShouldContain("attributes=customerId=eq:12345");
        jsonFilters.ShouldContain("attributes=amount=gt:1000");
    }

    [Fact]
    public void StatusFilter_WithMultipleValues_ShouldResolveAllValues()
    {
        // Arrange
        var parameterIndex = 0;

        // Act
        var (condition, parameters) = InstanceColumnConditionBuilder.BuildCondition(
            "Status", "in", "Active,Busy,F", ref parameterIndex);

        // Assert
        condition.ShouldBe("s.\"Status\" IN ({0}, {1}, {2})");
        parameters.Count.ShouldBe(3);
        parameters[0].Value.ShouldBe("A"); // Active -> A
        parameters[1].Value.ShouldBe("B"); // Busy -> B
        parameters[2].Value.ShouldBe("F"); // F -> F (already a code)
    }

    [Fact]
    public void ParameterIndex_ShouldIncrementCorrectly()
    {
        // Arrange
        var parameterIndex = 5; // Start from 5

        // Act
        var (condition1, parameters1) = InstanceColumnConditionBuilder.BuildCondition(
            "Key", "eq", "test", ref parameterIndex);
        var (condition2, parameters2) = InstanceColumnConditionBuilder.BuildCondition(
            "Status", "eq", "Active", ref parameterIndex);

        // Assert
        condition1.ShouldBe("s.\"Key\" = {5}");
        condition2.ShouldBe("s.\"Status\" = {6}");
        parameterIndex.ShouldBe(7);
    }

    #endregion
}

