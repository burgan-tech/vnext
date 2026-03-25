using System;
using System.Collections.Generic;
using BBT.Workflow.Definitions.GraphQL;
using Npgsql;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Unit tests for GraphQLJsonFilterService
/// These tests focus on SQL generation logic
/// </summary>
public class GraphQLJsonFilterServiceTests : DomainTestBase<DomainEntryPoint>
{
    #region BuildWhereClause Tests

    [Fact]
    public void BuildWhereClause_ShouldReturnEmpty_WhenEmptyNode()
    {
        // Arrange
        var node = new GraphQLFilterNode();
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateEqualityCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"clientId":{"eq":"122"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("@>", result);
        Assert.NotEmpty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateNotEqualsCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"status":{"ne":"deleted"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("NOT", result);
        Assert.Contains("@>", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateGreaterThanCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"gt":100}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("::numeric", result);
        Assert.Contains(">", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateLessThanCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"amount":{"lt":50}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("<", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateStringGreaterThan_WithoutNumericCast()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"code":{"sgt":"M"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        Assert.NotEmpty(result);
        Assert.Contains(">", result);
        Assert.DoesNotContain("::numeric", result);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateDateGreaterThan_WithTimestamptzCast()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"startedAt":{"dgt":"2024-01-01T00:00:00Z"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        Assert.NotEmpty(result);
        Assert.Contains("::timestamptz", result);
        Assert.Contains(">", result);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateDateGreaterThan_FromUnixSecondsNumber()
    {
        const long unixSeconds = 1_704_067_200L;
        var node = GraphQLFilterParser.ParseFilter(
            $"{{\"attributes\":{{\"startedAt\":{{\"dgt\":{unixSeconds}}}}}}}");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        Assert.NotEmpty(result);
        Assert.Contains("::timestamptz", result);
        Assert.Single(parameters);
        var p = parameters[0];
        Assert.Equal(NpgsqlTypes.NpgsqlDbType.TimestampTz, p.NpgsqlDbType);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, p.Value);
    }

    [Fact]
    public void BuildWhereClause_ContainsScalar_ShouldMatchPrimitiveJsonArray()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"invitedUser":{"contains":"pm-001"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        Assert.NotEmpty(result);
        Assert.Contains("@>", result);
        Assert.Single(parameters);
        var payload = parameters[0].Value?.ToString() ?? "";
        Assert.Contains("pm-001", payload);
    }

    [Fact]
    public void BuildWhereClause_ContainsObject_ShouldProduceSingleElementObjectArrayJsonb()
    {
        var node = GraphQLFilterParser.ParseFilter(
            """{"attributes":{"members":{"contains":{"type":"member","memberId":"X"}}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        Assert.NotEmpty(result);
        Assert.Contains("@>", result);
        Assert.Single(parameters);
        var payload = parameters[0].Value?.ToString() ?? "";
        Assert.Contains("\"type\"", payload);
        Assert.Contains("member", payload);
        Assert.Contains("\"memberId\"", payload);
        Assert.Contains("\"X\"", payload);
        Assert.False(payload.Contains("\"{\\\"type\\\"\""), "Payload must not double-encode the object as a string.");
    }

    [Fact]
    public void BuildWhereClause_ContainsJsonArrayValue_ShouldThrow()
    {
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"invitedUser":{"contains":["pm-001"]}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        Assert.Throws<ArgumentException>(() =>
            GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex));
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateBetweenCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"age":{"between":[18, 65]}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("BETWEEN", result);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateLikeCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"name":{"like":"John"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("ILIKE", result);
        Assert.Single(parameters);
        Assert.Contains("%John%", parameters[0].Value?.ToString());
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateStartsWithCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"email":{"startswith":"test@"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("ILIKE", result);
        Assert.Single(parameters);
        Assert.Equal("test@%", parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateEndsWithCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"domain":{"endswith":".com"}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("ILIKE", result);
        Assert.Single(parameters);
        Assert.Equal("%.com", parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateInCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"status":{"in":["active","pending"]}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("IN", result);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateNotInCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"status":{"nin":["cancelled","deleted"]}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("NOT IN", result);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateIsNullCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"optionalField":{"isNull":true}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("IS NULL", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateIsNotNullCondition()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""{"attributes":{"requiredField":{"isNull":false}}}""");
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("IS NOT NULL", result);
    }

    #endregion

    #region Logical Operator Tests

    [Fact]
    public void BuildWhereClause_ShouldGenerateAndClause()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""
        {
            "and": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"amount":{"gt":100}}}
            ]
        }
        """);
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(" AND ", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateOrClause()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""
        {
            "or": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"status":{"eq":"pending"}}}
            ]
        }
        """);
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(" OR ", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateNotClause()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""
        {
            "not": {"attributes":{"status":{"eq":"deleted"}}}
        }
        """);
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.StartsWith("NOT (", result);
    }

    [Fact]
    public void BuildWhereClause_ShouldHandleComplexNestedLogic()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""
        {
            "and": [
                {
                    "or": [
                        {"attributes":{"status":{"eq":"active"}}},
                        {"attributes":{"status":{"eq":"pending"}}}
                    ]
                },
                {"attributes":{"amount":{"gt":100}}}
            ]
        }
        """);
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(" AND ", result);
        Assert.Contains(" OR ", result);
    }

    #endregion

    #region Nested Field Tests

    [Fact]
    public void BuildWhereClause_ShouldHandleNestedFieldPath()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Attributes = new Dictionary<string, FieldCondition>
            {
                ["parent.child"] = new FieldCondition { Eq = "value" }
            }
        };
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref parameterIndex);

        // Assert — nested equality uses top-level jsonb @> with a nested object pattern (not #>> text extract)
        Assert.NotEmpty(result);
        Assert.Contains("@>", result);
        Assert.Contains("\"Data\"", result);
        Assert.Single(parameters);
        var payload = parameters[0].Value?.ToString() ?? "";
        Assert.Contains("parent", payload);
        Assert.Contains("child", payload);
        Assert.Contains("value", payload);
    }

    [Fact]
    public void BuildWhereClause_ShouldHandleDeeplyNestedFieldPath()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Attributes = new Dictionary<string, FieldCondition>
            {
                ["level1.level2.level3"] = new FieldCondition { Eq = "value" }
            }
        };
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref parameterIndex);

        // Assert — path segments appear in the jsonb containment payload, not as SQL #>> literals
        Assert.NotEmpty(result);
        Assert.Contains("@>", result);
        Assert.NotEmpty(parameters);
        var payload = parameters[0].Value?.ToString() ?? "";
        Assert.Contains("level1", payload);
        Assert.Contains("level2", payload);
        Assert.Contains("level3", payload);
    }

    #endregion

    #region Multiple Conditions Tests

    [Fact]
    public void BuildWhereClause_ShouldCombineMultipleFieldConditions()
    {
        // Arrange
        var node = GraphQLFilterParser.ParseFilter("""
        {
            "attributes": {
                "status": {"eq": "active"},
                "amount": {"gt": 100},
                "category": {"in": ["A", "B"]}
            }
        }
        """);
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act
        var result = GraphQLJsonFilterService.BuildWhereClause(node!, "Data", parameters, ref parameterIndex);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains(" AND ", result);
        Assert.True(parameters.Count >= 4); // At least 1 for status, 1 for amount, 2 for category
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public void BuildWhereClause_ShouldThrowOnInvalidFieldName()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Attributes = new Dictionary<string, FieldCondition>
            {
                ["invalid; DROP TABLE users;--"] = new FieldCondition { Eq = "value" }
            }
        };
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref parameterIndex));
    }

    [Fact]
    public void BuildWhereClause_ShouldThrowOnNonNumericValueForGt()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Attributes = new Dictionary<string, FieldCondition>
            {
                ["amount"] = new FieldCondition { Gt = "not-a-number" }
            }
        };
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref parameterIndex));
    }

    [Fact]
    public void BuildWhereClause_ShouldThrowOnInvalidBetweenValues()
    {
        // Arrange - between with only one value
        var node = new GraphQLFilterNode
        {
            Attributes = new Dictionary<string, FieldCondition>
            {
                ["age"] = new FieldCondition { Between = new object[] { 18 } }
            }
        };
        var parameters = new List<NpgsqlParameter>();
        var parameterIndex = 0;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            GraphQLJsonFilterService.BuildWhereClause(node, "Data", parameters, ref parameterIndex));
    }

    #endregion
}




