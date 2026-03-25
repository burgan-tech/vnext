using System;
using System.Linq;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// Unit tests for PostgreSqlJsonFilterService
/// These tests focus on the parsing and SQL generation logic
/// Database-specific functionality would require integration tests
/// </summary>
public class PostgreSqlJsonFilterServiceTests : DomainTestBase<DomainEntryPoint>
{
    private class TestEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [Fact]
    public void BuildFilteredQuery_ShouldReturnEmptyQuery_WhenNoFilters()
    {
        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            null!, "Json", "TestEntities");

        // Assert
        Assert.Empty(sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldReturnEmptyQuery_WhenEmptyFilters()
    {
        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            Array.Empty<string>(), "Json", "TestEntities");

        // Assert
        Assert.Empty(sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForEqualityFilter()
    {
        // Arrange
        var filters = new[] { "name=eq:John" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("SELECT * FROM \"TestEntities\"", sql);
        Assert.NotEmpty(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForGreaterThanFilter()
    {
        // Arrange
        var filters = new[] { "age=gt:25" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains(">", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForLessThanFilter()
    {
        // Arrange
        var filters = new[] { "age=lt:50" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("<", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForStringGreaterThan_WithoutNumericCast()
    {
        var filters = new[] { "code=sgt:M" };

        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        Assert.NotEmpty(sql);
        Assert.Contains(">", sql);
        Assert.DoesNotContain("::numeric", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForDateGreaterThan_WithTimestamptzCast()
    {
        var filters = new[] { "startedAt=dgt:2024-01-01" };

        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        Assert.NotEmpty(sql);
        Assert.Contains("::timestamptz", sql);
        Assert.Contains(">", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForDateGreaterThan_WithUnixEpochSeconds()
    {
        const long unixSeconds = 1_704_067_200L;
        var filters = new[] { $"startedAt=dgt:{unixSeconds}" };

        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        Assert.NotEmpty(sql);
        Assert.Contains("::timestamptz", sql);
        Assert.Single(parameters);
        Assert.Equal(System.DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, parameters[0].Value);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForBetweenFilter()
    {
        // Arrange
        var filters = new[] { "age=between:20,30" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("BETWEEN", sql);
        Assert.Equal(2, parameters.Length);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForLikeFilter()
    {
        // Arrange
        var filters = new[] { "name=like:John" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("ILIKE", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForStartsWithFilter()
    {
        // Arrange
        var filters = new[] { "name=startswith:Jo" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("ILIKE", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForEndsWithFilter()
    {
        // Arrange
        var filters = new[] { "name=endswith:son" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("ILIKE", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForInFilter()
    {
        // Arrange
        var filters = new[] { "status=in:active,pending,completed" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("IN", sql);
        Assert.Equal(3, parameters.Length);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldGenerateSql_ForNotInFilter()
    {
        // Arrange
        var filters = new[] { "status=nin:cancelled,rejected" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("NOT IN", sql);
        Assert.Equal(2, parameters.Length);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldCombineMultipleFilters()
    {
        // Arrange
        var filters = new[] 
        { 
            "name=eq:John",
            "age=gt:25"
        };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("AND", sql);
        Assert.True(parameters.Length >= 2);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleInvalidFilter()
    {
        // Arrange
        var filters = new[] { "invalid-format" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert - Should return empty results for invalid filters
        Assert.Empty(sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldUseDefaultTableName()
    {
        // Arrange
        var filters = new[] { "name=eq:John" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("TestEntitys", sql); // Default convention: EntityName + "s"
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleCustomTableName()
    {
        // Arrange
        var filters = new[] { "name=eq:John" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "CustomTable");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("CustomTable", sql);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleCustomJsonColumnName()
    {
        // Arrange
        var filters = new[] { "name=eq:John" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "CustomData", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("\"CustomData\"", sql);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleNotEqualsFilter()
    {
        // Arrange
        var filters = new[] { "name=ne:John" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("NOT", sql);
        Assert.NotEmpty(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleGreaterThanOrEqualFilter()
    {
        // Arrange
        var filters = new[] { "age=ge:25" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains(">=", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleLessThanOrEqualFilter()
    {
        // Arrange
        var filters = new[] { "age=le:50" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("<=", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildFilteredQuery_ShouldHandleMatchOperator()
    {
        // Arrange
        var filters = new[] { "description=match:keyword" };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.Contains("ILIKE", sql);
        Assert.Single(parameters);
    }

    [Theory]
    [InlineData("field=eq:value")]
    [InlineData("field=ne:value")]
    [InlineData("field=gt:10")]
    [InlineData("field=ge:10")]
    [InlineData("field=lt:10")]
    [InlineData("field=le:10")]
    [InlineData("field=between:10,20")]
    [InlineData("field=like:value")]
    [InlineData("field=match:value")]
    [InlineData("field=startswith:val")]
    [InlineData("field=endswith:lue")]
    [InlineData("field=in:a,b,c")]
    [InlineData("field=nin:x,y,z")]
    public void BuildFilteredQuery_ShouldHandleAllSupportedOperators(string filter)
    {
        // Arrange
        var filters = new[] { filter };

        // Act
        var (sql, parameters) = PostgreSqlJsonFilterService.BuildFilteredQuery<TestEntity>(
            filters, "Json", "TestEntities");

        // Assert
        Assert.NotEmpty(sql);
        Assert.NotEmpty(parameters);
    }
}

