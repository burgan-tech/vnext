using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions.GraphQL;
using Npgsql;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// End-to-end tests for array-valued filter operators (in, nin, between) on Instance columns.
/// These operators arrive as JSON arrays (object[]) and must be flattened to a comma-separated
/// string before reaching InstanceColumnConditionBuilder — otherwise the array collapses to the
/// literal "System.Object[]" and the filter silently matches nothing.
/// </summary>
public class InstanceColumnArrayFilterTests
{
    private static (string instanceWhere, List<NpgsqlParameter> parameters) BuildInstanceWhere(string json)
    {
        var node = GraphQLFilterParser.ParseFilter(json);
        node.ShouldNotBeNull();

        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;
        var (_, instanceWhere) = GraphQLJsonFilterService.BuildSeparatedWhereClausesForSql(
            node, "Data", parameters, ref parameterIndex);

        return (instanceWhere, parameters);
    }

    [Fact]
    public void In_OnInstanceColumn_WithJsonArray_ShouldBuildInClause()
    {
        // Arrange
        var json = """{"key":{"in":["k1","k2","k3"]}}""";

        // Act
        var (instanceWhere, parameters) = BuildInstanceWhere(json);

        // Assert
        instanceWhere.ShouldBe("s.\"Key\" IN ({0}, {1}, {2})");
        parameters.Count.ShouldBe(3);
        parameters.Select(p => p.Value).ShouldBe(new object[] { "k1", "k2", "k3" });
        // Regression guard: the array must never collapse to "System.Object[]"
        parameters.ShouldNotContain(p => Equals(p.Value, "System.Object[]"));
    }

    [Fact]
    public void In_OnStatusColumn_WithJsonArray_ShouldResolveStatusCodes()
    {
        // Arrange
        var json = """{"status":{"in":["Active","Busy"]}}""";

        // Act
        var (instanceWhere, parameters) = BuildInstanceWhere(json);

        // Assert
        instanceWhere.ShouldBe("s.\"Status\" IN ({0}, {1})");
        parameters.Count.ShouldBe(2);
        parameters[0].Value.ShouldBe("A"); // Active -> A
        parameters[1].Value.ShouldBe("B"); // Busy -> B
    }

    [Fact]
    public void NotIn_OnInstanceColumn_WithJsonArray_ShouldBuildNotInClause()
    {
        // Arrange
        var json = """{"key":{"nin":["k1","k2"]}}""";

        // Act
        var (instanceWhere, parameters) = BuildInstanceWhere(json);

        // Assert
        instanceWhere.ShouldBe("s.\"Key\" NOT IN ({0}, {1})");
        parameters.Count.ShouldBe(2);
        parameters.Select(p => p.Value).ShouldBe(new object[] { "k1", "k2" });
    }

    [Fact]
    public void Between_OnInstanceColumn_WithJsonArray_ShouldBuildBetweenClause()
    {
        // Arrange
        var json = """{"createdAt":{"between":["2024-01-01","2024-12-31"]}}""";

        // Act
        var (instanceWhere, parameters) = BuildInstanceWhere(json);

        // Assert
        instanceWhere.ShouldBe("s.\"CreatedAt\" BETWEEN {0} AND {1}");
        parameters.Count.ShouldBe(2);
    }

    [Fact]
    public void IsNull_OnInstanceColumn_ShouldBuildIsNullClause()
    {
        // Arrange
        var json = """{"completedAt":{"isNull":true}}""";

        // Act
        var (instanceWhere, parameters) = BuildInstanceWhere(json);

        // Assert
        instanceWhere.ShouldBe("s.\"CompletedAt\" IS NULL");
        parameters.ShouldBeEmpty();
    }

    [Fact]
    public void IsNull_False_OnInstanceColumn_ShouldBuildIsNotNullClause()
    {
        // Arrange
        var json = """{"completedAt":{"isNull":false}}""";

        // Act
        var (instanceWhere, parameters) = BuildInstanceWhere(json);

        // Assert
        instanceWhere.ShouldBe("s.\"CompletedAt\" IS NOT NULL");
        parameters.ShouldBeEmpty();
    }
}
