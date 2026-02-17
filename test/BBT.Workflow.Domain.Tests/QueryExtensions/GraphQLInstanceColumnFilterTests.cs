using System;
using System.Text.Json;
using BBT.Workflow.Definitions.GraphQL;
using Xunit;
using Shouldly;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// Tests for GraphQL format with Instance columns at root level
/// Validates that {"key":{"eq":"1111"}} format works correctly
/// </summary>
public class GraphQLInstanceColumnFilterTests
{
    [Fact]
    public void ParseFilter_WithRootLevelKey_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{""key"":{""eq"":""1111""}}";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        filterNode.Attributes.ShouldContainKey("key");
        filterNode.Attributes["key"].Eq.ShouldBe("1111");
    }

    [Fact]
    public void ParseFilter_WithRootLevelStatus_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{""status"":{""eq"":""Active""}}";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        filterNode.Attributes.ShouldContainKey("status");
        filterNode.Attributes["status"].Eq.ShouldBe("Active");
    }

    [Fact]
    public void ParseFilter_WithMixedRootAndAttributes_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{
            ""key"":{""eq"":""1111""},
            ""status"":{""eq"":""Active""},
            ""attributes"":{
                ""triggerId"":{""eq"":""82111090771""}
            }
        }";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        
        // Root level fields
        filterNode.Attributes.ShouldContainKey("key");
        filterNode.Attributes["key"].Eq.ShouldBe("1111");
        
        filterNode.Attributes.ShouldContainKey("status");
        filterNode.Attributes["status"].Eq.ShouldBe("Active");
        
        // Nested attributes field
        filterNode.Attributes.ShouldContainKey("triggerId");
        filterNode.Attributes["triggerId"].Eq.ShouldBe("82111090771");
    }

    [Fact]
    public void ParseFilter_WithRootLevelCreatedAt_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{""createdAt"":{""gt"":""2024-01-01""}}";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        filterNode.Attributes.ShouldContainKey("createdAt");
        filterNode.Attributes["createdAt"].Gt.ShouldBe("2024-01-01");
    }

    [Fact]
    public void ParseFilter_WithRootLevelFlow_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{""flow"":{""like"":""workflow""}}";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        filterNode.Attributes.ShouldContainKey("flow");
        filterNode.Attributes["flow"].Like.ShouldBe("workflow");
    }

    [Fact]
    public void ParseFilter_WithMultipleRootLevelInstanceColumns_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{
            ""key"":{""eq"":""1111""},
            ""status"":{""in"":[""Active"",""Busy""]},
            ""flow"":{""like"":""payment""},
            ""createdAt"":{""between"":[""2024-01-01"",""2024-12-31""]}
        }";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        filterNode.Attributes.Count.ShouldBe(4);
        
        filterNode.Attributes["key"].Eq.ShouldBe("1111");
        filterNode.Attributes["status"].In.ShouldNotBeNull();
        filterNode.Attributes["flow"].Like.ShouldBe("payment");
        filterNode.Attributes["createdAt"].Between.ShouldNotBeNull();
    }

    [Fact]
    public void ParseFilter_WithAndOperatorAndRootLevelColumns_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{
            ""and"":[
                {""key"":{""eq"":""1111""}},
                {""status"":{""eq"":""Active""}}
            ]
        }";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.And);
        filterNode.And.ShouldNotBeNull();
        filterNode.And.Count.ShouldBe(2);
        
        // First condition
        filterNode.And[0].NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.And[0].Attributes.ShouldContainKey("key");
        
        // Second condition
        filterNode.And[1].NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.And[1].Attributes.ShouldContainKey("status");
    }

    [Fact]
    public void ParseFilter_WithOrOperatorAndRootLevelColumns_ShouldParseCorrectly()
    {
        // Arrange
        var filterJson = @"{
            ""or"":[
                {""key"":{""eq"":""1111""}},
                {""key"":{""eq"":""2222""}}
            ]
        }";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Or);
        filterNode.Or.ShouldNotBeNull();
        filterNode.Or.Count.ShouldBe(2);
    }

    [Fact]
    public void ParseFilter_BackwardCompatibility_AttributesFormat_ShouldStillWork()
    {
        // Arrange - Old format should still work
        var filterJson = @"{""attributes"":{""triggerId"":{""eq"":""82111090771""}}}";

        // Act
        var filterNode = GraphQLFilterParser.ParseFilter(filterJson);

        // Assert
        filterNode.ShouldNotBeNull();
        filterNode.NodeType.ShouldBe(FilterNodeType.Condition);
        filterNode.Attributes.ShouldNotBeNull();
        filterNode.Attributes.ShouldContainKey("triggerId");
        filterNode.Attributes["triggerId"].Eq.ShouldBe("82111090771");
    }
}

