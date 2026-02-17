using System;
using System.Linq;
using BBT.Workflow.Definitions.GraphQL;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Unit tests for GraphQLFilterParser
/// </summary>
public class GraphQLFilterParserTests : DomainTestBase<DomainEntryPoint>
{
    #region ParseFilter Tests

    [Fact]
    public void ParseFilter_ShouldReturnNull_WhenNullInput()
    {
        // Act
        var result = GraphQLFilterParser.ParseFilter(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFilter_ShouldReturnNull_WhenEmptyInput()
    {
        // Act
        var result = GraphQLFilterParser.ParseFilter("");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFilter_ShouldReturnNull_WhenWhitespaceInput()
    {
        // Act
        var result = GraphQLFilterParser.ParseFilter("   ");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseFilter_ShouldParseSimpleEquality()
    {
        // Arrange
        var json = """{"attributes":{"clientId":{"eq":122}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Condition, result.NodeType);
        Assert.NotNull(result.Attributes);
        Assert.True(result.Attributes.ContainsKey("clientId"));
        Assert.Equal(122L, result.Attributes["clientId"].Eq);
    }

    [Fact]
    public void ParseFilter_ShouldParseStringEquality()
    {
        // Arrange
        var json = """{"attributes":{"status":{"eq":"active"}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Condition, result.NodeType);
        Assert.NotNull(result.Attributes);
        Assert.Equal("active", result.Attributes["status"].Eq);
    }

    [Fact]
    public void ParseFilter_ShouldParseMultipleConditions()
    {
        // Arrange
        var json = """{"attributes":{"clientId":{"eq":122},"testValue":{"gt":2}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Condition, result.NodeType);
        Assert.NotNull(result.Attributes);
        Assert.Equal(2, result.Attributes.Count);
        Assert.Equal(122L, result.Attributes["clientId"].Eq);
        Assert.Equal(2L, result.Attributes["testValue"].Gt);
    }

    [Fact]
    public void ParseFilter_ShouldParseAndOperator()
    {
        // Arrange
        var json = """
        {
            "and": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"amount":{"gt":100}}}
            ]
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.And, result.NodeType);
        Assert.NotNull(result.And);
        Assert.Equal(2, result.And.Count);
    }

    [Fact]
    public void ParseFilter_ShouldParseOrOperator()
    {
        // Arrange
        var json = """
        {
            "or": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"status":{"eq":"pending"}}}
            ]
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Or, result.NodeType);
        Assert.NotNull(result.Or);
        Assert.Equal(2, result.Or.Count);
    }

    [Fact]
    public void ParseFilter_ShouldParseNotOperator()
    {
        // Arrange
        var json = """
        {
            "not": {"attributes":{"status":{"eq":"deleted"}}}
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.Not, result.NodeType);
        Assert.NotNull(result.Not);
        Assert.Equal(FilterNodeType.Condition, result.Not.NodeType);
    }

    [Fact]
    public void ParseFilter_ShouldParseComplexNestedLogic()
    {
        // Arrange
        var json = """
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
        """;

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(FilterNodeType.And, result.NodeType);
        Assert.NotNull(result.And);
        Assert.Equal(2, result.And.Count);
        Assert.Equal(FilterNodeType.Or, result.And[0].NodeType);
        Assert.Equal(FilterNodeType.Condition, result.And[1].NodeType);
    }

    [Fact]
    public void ParseFilter_ShouldParseAllComparisonOperators()
    {
        // Arrange
        var json = """
        {
            "attributes": {
                "field1": {"eq": 1},
                "field2": {"ne": 2},
                "field3": {"gt": 3},
                "field4": {"ge": 4},
                "field5": {"lt": 5},
                "field6": {"le": 6}
            }
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.Equal(6, result.Attributes.Count);
        Assert.Equal(1L, result.Attributes["field1"].Eq);
        Assert.Equal(2L, result.Attributes["field2"].Ne);
        Assert.Equal(3L, result.Attributes["field3"].Gt);
        Assert.Equal(4L, result.Attributes["field4"].Ge);
        Assert.Equal(5L, result.Attributes["field5"].Lt);
        Assert.Equal(6L, result.Attributes["field6"].Le);
    }

    [Fact]
    public void ParseFilter_ShouldParseStringOperators()
    {
        // Arrange
        var json = """
        {
            "attributes": {
                "name": {"like": "John"},
                "email": {"startswith": "test@"},
                "domain": {"endswith": ".com"}
            }
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.Equal("John", result.Attributes["name"].Like);
        Assert.Equal("test@", result.Attributes["email"].StartsWith);
        Assert.Equal(".com", result.Attributes["domain"].EndsWith);
    }

    [Fact]
    public void ParseFilter_ShouldParseBetweenOperator()
    {
        // Arrange
        var json = """{"attributes":{"age":{"between":[18, 65]}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.NotNull(result.Attributes["age"].Between);
        Assert.Equal(2, result.Attributes["age"].Between!.Length);
    }

    [Fact]
    public void ParseFilter_ShouldParseInOperator()
    {
        // Arrange
        var json = """{"attributes":{"status":{"in":["active","pending","processing"]}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.NotNull(result.Attributes["status"].In);
        Assert.Equal(3, result.Attributes["status"].In!.Length);
    }

    [Fact]
    public void ParseFilter_ShouldParseNotInOperator()
    {
        // Arrange
        var json = """{"attributes":{"status":{"nin":["cancelled","deleted"]}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.NotNull(result.Attributes["status"].NotIn);
        Assert.Equal(2, result.Attributes["status"].NotIn!.Length);
    }

    [Fact]
    public void ParseFilter_ShouldParseIsNullTrue()
    {
        // Arrange
        var json = """{"attributes":{"optionalField":{"isNull":true}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.True(result.Attributes["optionalField"].IsNull);
    }

    [Fact]
    public void ParseFilter_ShouldParseIsNullFalse()
    {
        // Arrange
        var json = """{"attributes":{"requiredField":{"isNull":false}}}""";

        // Act
        var result = GraphQLFilterParser.ParseFilter(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Attributes);
        Assert.False(result.Attributes["requiredField"].IsNull);
    }

    [Fact]
    public void ParseFilter_ShouldThrowOnInvalidJson()
    {
        // Arrange
        var json = "not valid json {{{";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => GraphQLFilterParser.ParseFilter(json));
    }

    #endregion

    #region ParseGroupBy Tests

    [Fact]
    public void ParseGroupBy_ShouldReturnNull_WhenNullInput()
    {
        // Act
        var result = GraphQLFilterParser.ParseGroupBy(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseGroupBy_ShouldParseSingleField()
    {
        // Arrange
        var json = """{"field":"attributes.status"}""";

        // Act
        var result = GraphQLFilterParser.ParseGroupBy(json);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("attributes.status", result.Field);
    }

    [Fact]
    public void ParseGroupBy_ShouldParseMultipleFields()
    {
        // Arrange
        var json = """{"fields":["attributes.status","attributes.category"]}""";

        // Act
        var result = GraphQLFilterParser.ParseGroupBy(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Fields);
        Assert.Equal(2, result.Fields.Count);
    }

    [Fact]
    public void ParseGroupBy_ShouldParseWithAggregations()
    {
        // Arrange
        var json = """
        {
            "field": "attributes.status",
            "aggregations": {
                "count": true,
                "sum": "attributes.amount"
            }
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseGroupBy(json);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Aggregations);
        Assert.True(result.Aggregations.Count is bool b && b);
        Assert.Equal("attributes.amount", result.Aggregations.Sum);
    }

    [Fact]
    public void ParseGroupBy_GetFields_ShouldCombineSingleAndMultipleFields()
    {
        // Arrange
        var json = """
        {
            "field": "attributes.status",
            "fields": ["attributes.category", "attributes.type"]
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseGroupBy(json);
        var fields = result!.GetFields();

        // Assert
        Assert.Equal(3, fields.Count);
        Assert.Contains("attributes.status", fields);
        Assert.Contains("attributes.category", fields);
        Assert.Contains("attributes.type", fields);
    }

    #endregion

    #region ParseAggregations Tests

    [Fact]
    public void ParseAggregations_ShouldReturnNull_WhenNullInput()
    {
        // Act
        var result = GraphQLFilterParser.ParseAggregations(null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseAggregations_ShouldParseCount()
    {
        // Arrange
        var json = """{"count":true}""";

        // Act
        var result = GraphQLFilterParser.ParseAggregations(json);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Count is bool b && b);
    }

    [Fact]
    public void ParseAggregations_ShouldParseAllFunctions()
    {
        // Arrange
        var json = """
        {
            "count": true,
            "sum": "attributes.amount",
            "avg": "attributes.amount",
            "min": "attributes.amount",
            "max": "attributes.amount"
        }
        """;

        // Act
        var result = GraphQLFilterParser.ParseAggregations(json);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.HasAggregations);
        Assert.Equal("attributes.amount", result.Sum);
        Assert.Equal("attributes.amount", result.Avg);
        Assert.Equal("attributes.amount", result.Min);
        Assert.Equal("attributes.amount", result.Max);
    }

    #endregion

    #region FlattenToConditions Tests

    [Fact]
    public void FlattenToConditions_ShouldReturnEmpty_WhenNullNode()
    {
        // Act
        var result = GraphQLFilterParser.FlattenToConditions(null);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void FlattenToConditions_ShouldFlattenSimpleCondition()
    {
        // Arrange
        var json = """{"attributes":{"clientId":{"eq":"122"}}}""";
        var node = GraphQLFilterParser.ParseFilter(json);

        // Act
        var result = GraphQLFilterParser.FlattenToConditions(node);

        // Assert
        Assert.Single(result);
        Assert.Equal("clientId", result[0].Field);
        Assert.Equal("eq", result[0].Operator);
        Assert.Equal("122", result[0].Value);
    }

    [Fact]
    public void FlattenToConditions_ShouldFlattenMultipleConditions()
    {
        // Arrange
        var json = """{"attributes":{"clientId":{"eq":"122"},"status":{"ne":"deleted"}}}""";
        var node = GraphQLFilterParser.ParseFilter(json);

        // Act
        var result = GraphQLFilterParser.FlattenToConditions(node);

        // Assert
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FlattenToConditions_ShouldPreserveLegacyFormat()
    {
        // Arrange
        var json = """{"attributes":{"clientId":{"eq":"122"}}}""";
        var node = GraphQLFilterParser.ParseFilter(json);

        // Act
        var result = GraphQLFilterParser.FlattenToConditions(node);
        var legacyFormat = result[0].ToLegacyFormat();

        // Assert
        Assert.Equal("attributes=clientId=eq:122", legacyFormat);
    }

    #endregion

    #region ParseOrderBy Tests

    [Fact]
    public void ParseOrderBy_ShouldReturnNull_WhenNullInput()
    {
        var result = GraphQLFilterParser.ParseOrderBy(null);
        Assert.Null(result);
    }

    [Fact]
    public void ParseOrderBy_ShouldReturnNull_WhenEmptyInput()
    {
        var result = GraphQLFilterParser.ParseOrderBy("");
        Assert.Null(result);
    }

    [Fact]
    public void ParseOrderBy_ShouldParseSingleField_WithDirection()
    {
        var json = """{"field":"createdAt","direction":"desc"}""";
        var result = GraphQLFilterParser.ParseOrderBy(json);
        Assert.NotNull(result);
        var entries = result!.GetEntries();
        Assert.Single(entries);
        Assert.Equal("createdAt", entries[0].Field);
        Assert.Equal("desc", entries[0].Direction);
    }

    [Fact]
    public void ParseOrderBy_ShouldParseSingleField_DefaultAsc()
    {
        var json = """{"field":"status"}""";
        var result = GraphQLFilterParser.ParseOrderBy(json);
        Assert.NotNull(result);
        var entries = result!.GetEntries();
        Assert.Single(entries);
        Assert.Equal("asc", entries[0].Direction);
    }

    [Fact]
    public void ParseOrderBy_ShouldParseMultipleFields()
    {
        var json = """{"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}""";
        var result = GraphQLFilterParser.ParseOrderBy(json);
        Assert.NotNull(result);
        var entries = result!.GetEntries();
        Assert.Equal(2, entries.Count);
        Assert.Equal("status", entries[0].Field);
        Assert.Equal("asc", entries[0].Direction);
        Assert.Equal("createdAt", entries[1].Field);
        Assert.Equal("desc", entries[1].Direction);
    }

    [Fact]
    public void ParseOrderBy_ShouldReturnNull_WhenInvalidJson()
    {
        var result = GraphQLFilterParser.ParseOrderBy("not valid json");
        Assert.Null(result);
    }

    [Fact]
    public void ParseOrderBy_ShouldReturnNull_WhenEmptyField()
    {
        var result = GraphQLFilterParser.ParseOrderBy("""{"field":"","direction":"asc"}""");
        Assert.Null(result);
    }

    #endregion
}




