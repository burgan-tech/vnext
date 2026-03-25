using System;
using System.Linq;
using BBT.Workflow.Definitions.GraphQL;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions.GraphQL;

/// <summary>
/// Unit tests for UnifiedFilterService
/// </summary>
public class UnifiedFilterServiceTests : DomainTestBase<DomainEntryPoint>
{
    #region ValidateFilter Tests

    [Fact]
    public void ValidateFilter_ShouldReturnValid_WhenNullInput()
    {
        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(null);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnValid_WhenEmptyInput()
    {
        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter("");

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnValid_ForSimpleCondition()
    {
        // Arrange
        var filter = """{"attributes":{"clientId":{"eq":122}}}""";

        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(filter);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnValid_ForAndCondition()
    {
        // Arrange
        var filter = """
        {
            "and": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"amount":{"gt":100}}}
            ]
        }
        """;

        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(filter);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnValid_ForOrCondition()
    {
        // Arrange
        var filter = """
        {
            "or": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"status":{"eq":"pending"}}}
            ]
        }
        """;

        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(filter);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnValid_ForNotCondition()
    {
        // Arrange
        var filter = """{"not":{"attributes":{"status":{"eq":"deleted"}}}}""";

        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(filter);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnInvalid_ForInvalidJson()
    {
        // Arrange
        var filter = "not valid json {{{";

        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(filter);

        // Assert
        Assert.False(isValid);
        Assert.NotNull(errorMessage);
        Assert.Contains("Invalid", errorMessage);
    }

    [Fact]
    public void ValidateFilter_ShouldReturnValid_ForComplexNestedFilter()
    {
        // Arrange
        var filter = """
        {
            "and": [
                {
                    "or": [
                        {"attributes":{"status":{"eq":"active"}}},
                        {"attributes":{"status":{"eq":"pending"}}}
                    ]
                },
                {"not":{"attributes":{"deleted":{"eq":true}}}},
                {"attributes":{"amount":{"between":[100, 1000]}}}
            ]
        }
        """;

        // Act
        var (isValid, errorMessage) = UnifiedFilterService.ValidateFilter(filter);

        // Assert
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    #endregion

    #region BuildWhereClause Tests

    [Fact]
    public void BuildWhereClause_ShouldReturnEmpty_WhenNullFilter()
    {
        // Act
        var (whereClause, parameters) = UnifiedFilterService.BuildWhereClause(null);

        // Assert
        Assert.Empty(whereClause);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldReturnEmpty_WhenEmptyFilter()
    {
        // Act
        var (whereClause, parameters) = UnifiedFilterService.BuildWhereClause("");

        // Assert
        Assert.Empty(whereClause);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateSql_ForSimpleCondition()
    {
        // Arrange
        var filter = """{"attributes":{"clientId":{"eq":"122"}}}""";

        // Act
        var (whereClause, parameters) = UnifiedFilterService.BuildWhereClause(filter);

        // Assert
        Assert.NotEmpty(whereClause);
        Assert.NotEmpty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldGenerateSql_ForComplexCondition()
    {
        // Arrange
        var filter = """
        {
            "and": [
                {"attributes":{"status":{"eq":"active"}}},
                {"attributes":{"amount":{"gt":100}}}
            ]
        }
        """;

        // Act
        var (whereClause, parameters) = UnifiedFilterService.BuildWhereClause(filter);

        // Assert
        Assert.NotEmpty(whereClause);
        Assert.Contains(" AND ", whereClause);
        Assert.NotEmpty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ShouldUseCustomJsonColumnName()
    {
        // Arrange
        var filter = """{"attributes":{"status":{"eq":"active"}}}""";

        // Act
        var (whereClause, parameters) = UnifiedFilterService.BuildWhereClause(filter, "CustomData");

        // Assert
        Assert.NotEmpty(whereClause);
        Assert.Contains("\"CustomData\"", whereClause);
    }

    #endregion

    #region Aggregation Request Validation Tests

    [Fact]
    public void AggregationRequest_HasAggregations_ShouldReturnFalse_WhenEmpty()
    {
        // Arrange
        var request = new AggregationRequest();

        // Act & Assert
        Assert.False(request.HasAggregations);
    }

    [Fact]
    public void AggregationRequest_HasAggregations_ShouldReturnTrue_WhenCountSet()
    {
        // Arrange
        var request = new AggregationRequest { Count = true };

        // Act & Assert
        Assert.True(request.HasAggregations);
    }

    [Fact]
    public void AggregationRequest_HasAggregations_ShouldReturnTrue_WhenSumSet()
    {
        // Arrange
        var request = new AggregationRequest { Sum = "amount" };

        // Act & Assert
        Assert.True(request.HasAggregations);
    }

    [Fact]
    public void AggregationRequest_HasAggregations_ShouldReturnTrue_WhenAvgSet()
    {
        // Arrange
        var request = new AggregationRequest { Avg = "amount" };

        // Act & Assert
        Assert.True(request.HasAggregations);
    }

    [Fact]
    public void AggregationRequest_HasAggregations_ShouldReturnTrue_WhenMinSet()
    {
        // Arrange
        var request = new AggregationRequest { Min = "amount" };

        // Act & Assert
        Assert.True(request.HasAggregations);
    }

    [Fact]
    public void AggregationRequest_HasAggregations_ShouldReturnTrue_WhenMaxSet()
    {
        // Arrange
        var request = new AggregationRequest { Max = "amount" };

        // Act & Assert
        Assert.True(request.HasAggregations);
    }

    #endregion

    #region GroupByRequest Tests

    [Fact]
    public void GroupByRequest_GetFields_ShouldReturnEmpty_WhenNoFieldsSet()
    {
        // Arrange
        var request = new GroupByRequest();

        // Act
        var fields = request.GetFields();

        // Assert
        Assert.Empty(fields);
    }

    [Fact]
    public void GroupByRequest_GetFields_ShouldReturnSingleField()
    {
        // Arrange
        var request = new GroupByRequest { Field = "status" };

        // Act
        var fields = request.GetFields();

        // Assert
        Assert.Single(fields);
        Assert.Equal("status", fields[0]);
    }

    [Fact]
    public void GroupByRequest_GetFields_ShouldReturnMultipleFields()
    {
        // Arrange
        var request = new GroupByRequest
        {
            Fields = new System.Collections.Generic.List<string> { "status", "category" }
        };

        // Act
        var fields = request.GetFields();

        // Assert
        Assert.Equal(2, fields.Count);
    }

    [Fact]
    public void GroupByRequest_GetFields_ShouldCombineSingleAndMultiple()
    {
        // Arrange
        var request = new GroupByRequest
        {
            Field = "status",
            Fields = new System.Collections.Generic.List<string> { "category", "type" }
        };

        // Act
        var fields = request.GetFields();

        // Assert
        Assert.Equal(3, fields.Count);
        Assert.Contains("status", fields);
        Assert.Contains("category", fields);
        Assert.Contains("type", fields);
    }

    [Fact]
    public void GroupByRequest_GetFields_ShouldRemoveDuplicates()
    {
        // Arrange
        var request = new GroupByRequest
        {
            Field = "status",
            Fields = new System.Collections.Generic.List<string> { "status", "category" }
        };

        // Act
        var fields = request.GetFields();

        // Assert
        Assert.Equal(2, fields.Count); // status should not be duplicated
    }

    #endregion

    #region FieldCondition Tests

    [Fact]
    public void FieldCondition_GetOperators_ShouldReturnEmpty_WhenNoOperatorsSet()
    {
        // Arrange
        var condition = new FieldCondition();

        // Act
        var operators = condition.GetOperators().ToList();

        // Assert
        Assert.Empty(operators);
    }

    [Fact]
    public void FieldCondition_GetOperators_ShouldReturnSingleOperator()
    {
        // Arrange
        var condition = new FieldCondition { Eq = "test" };

        // Act
        var operators = condition.GetOperators().ToList();

        // Assert
        Assert.Single(operators);
        Assert.Equal("eq", operators[0].Operator);
        Assert.Equal("test", operators[0].Value);
    }

    [Fact]
    public void FieldCondition_GetOperators_ShouldReturnMultipleOperators()
    {
        // Arrange
        var condition = new FieldCondition
        {
            Gt = 10,
            Lt = 100
        };

        // Act
        var operators = condition.GetOperators().ToList();

        // Assert
        Assert.Equal(2, operators.Count);
    }

    [Fact]
    public void FieldCondition_GetOperators_ShouldReturnAllSetOperators()
    {
        // Arrange
        var condition = new FieldCondition
        {
            Eq = "value",
            Ne = "other",
            Gt = 10,
            Ge = 5,
            Lt = 100,
            Le = 50,
            Like = "pattern",
            StartsWith = "prefix",
            EndsWith = "suffix",
            In = new object[] { "a", "b" },
            NotIn = new object[] { "x", "y" },
            IsNull = false
        };

        // Act
        var operators = condition.GetOperators().ToList();

        // Assert
        Assert.Equal(12, operators.Count);
    }

    #endregion

    #region GraphQLFilterNode Tests

    [Fact]
    public void GraphQLFilterNode_NodeType_ShouldReturnEmpty_WhenNoPropertiesSet()
    {
        // Arrange
        var node = new GraphQLFilterNode();

        // Act & Assert
        Assert.Equal(FilterNodeType.Empty, node.NodeType);
    }

    [Fact]
    public void GraphQLFilterNode_NodeType_ShouldReturnAnd_WhenAndSet()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            And = new System.Collections.Generic.List<GraphQLFilterNode>
            {
                new GraphQLFilterNode()
            }
        };

        // Act & Assert
        Assert.Equal(FilterNodeType.And, node.NodeType);
    }

    [Fact]
    public void GraphQLFilterNode_NodeType_ShouldReturnOr_WhenOrSet()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Or = new System.Collections.Generic.List<GraphQLFilterNode>
            {
                new GraphQLFilterNode()
            }
        };

        // Act & Assert
        Assert.Equal(FilterNodeType.Or, node.NodeType);
    }

    [Fact]
    public void GraphQLFilterNode_NodeType_ShouldReturnNot_WhenNotSet()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Not = new GraphQLFilterNode()
        };

        // Act & Assert
        Assert.Equal(FilterNodeType.Not, node.NodeType);
    }

    [Fact]
    public void GraphQLFilterNode_NodeType_ShouldReturnCondition_WhenAttributesSet()
    {
        // Arrange
        var node = new GraphQLFilterNode
        {
            Attributes = new System.Collections.Generic.Dictionary<string, FieldCondition>
            {
                ["field"] = new FieldCondition { Eq = "value" }
            }
        };

        // Act & Assert
        Assert.Equal(FilterNodeType.Condition, node.NodeType);
    }

    #endregion
}

