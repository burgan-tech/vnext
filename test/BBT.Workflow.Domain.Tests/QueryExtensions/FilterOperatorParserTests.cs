using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// Unit tests for FilterOperatorParser
/// </summary>
public class FilterOperatorParserTests : DomainTestBase<DomainEntryPoint>
{
    private class TestEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public decimal Price { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    #region ParseOperator Tests

    [Theory]
    [InlineData("name=eq:John", "name", "eq", "John")]
    [InlineData("age=gt:25", "age", "gt", "25")]
    [InlineData("code=sgt:M", "code", "sgt", "M")]
    [InlineData("startedAt=dgt:2024-01-01", "startedAt", "dgt", "2024-01-01")]
    [InlineData("price=lt:100.50", "price", "lt", "100.50")]
    [InlineData("status=ne:active", "status", "ne", "active")]
    public void ParseOperator_ShouldParseValidOperator(string input, string expectedField, string expectedOp, string expectedValue)
    {
        // Act
        var (field, op, value) = FilterOperatorParser.ParseOperator(input);

        // Assert
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedOp, op);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public void ParseOperator_ShouldHandleAttributesPrefix()
    {
        // Arrange
        var input = "attributes=name=eq:John";

        // Act
        var (field, op, value) = FilterOperatorParser.ParseOperator(input);

        // Assert
        Assert.Equal("name", field);
        Assert.Equal("eq", op);
        Assert.Equal("John", value);
    }

    [Fact]
    public void ParseOperator_ShouldFallbackToSimpleEquals()
    {
        // Arrange
        var input = "name=John";

        // Act
        var (field, op, value) = FilterOperatorParser.ParseOperator(input);

        // Assert
        Assert.Equal("name", field);
        Assert.Equal("eq", op);
        Assert.Equal("John", value);
    }

    [Fact]
    public void ParseOperator_ShouldThrowOnInvalidFormat()
    {
        // Arrange
        var input = "invalidformat";

        // Act & Assert
        Assert.Throws<ArgumentException>(() => FilterOperatorParser.ParseOperator(input));
    }

    [Theory]
    [InlineData("name=between:10,20", "name", "between", "10,20")]
    [InlineData("price=in:10,20,30", "price", "in", "10,20,30")]
    [InlineData("status=nin:active,inactive", "status", "nin", "active,inactive")]
    public void ParseOperator_ShouldHandleComplexOperators(string input, string expectedField, string expectedOp, string expectedValue)
    {
        // Act
        var (field, op, value) = FilterOperatorParser.ParseOperator(input);

        // Assert
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedOp, op);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("name=match:test", "name", "match", "test")]
    [InlineData("description=like:keyword", "description", "like", "keyword")]
    [InlineData("name=startswith:Jo", "name", "startswith", "Jo")]
    [InlineData("email=endswith:@example.com", "email", "endswith", "@example.com")]
    public void ParseOperator_ShouldHandleStringOperators(string input, string expectedField, string expectedOp, string expectedValue)
    {
        // Act
        var (field, op, value) = FilterOperatorParser.ParseOperator(input);

        // Assert
        Assert.Equal(expectedField, field);
        Assert.Equal(expectedOp, op);
        Assert.Equal(expectedValue, value);
    }

    #endregion

    #region CreateSimplePropertyExpression Tests

    [Fact]
    public void CreateSimplePropertyExpression_Equals_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "eq", "John");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("John", result[0].Name);
    }

    [Fact]
    public void CreateSimplePropertyExpression_NotEquals_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "ne", "John");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, e => e.Name == "John");
    }

    [Fact]
    public void CreateSimplePropertyExpression_GreaterThan_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "gt", "25");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Age > 25));
    }

    [Fact]
    public void CreateSimplePropertyExpression_GreaterThanOrEqual_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "ge", "28");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Age >= 28));
    }

    [Fact]
    public void CreateSimplePropertyExpression_LessThan_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "lt", "30");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Age < 30));
    }

    [Fact]
    public void CreateSimplePropertyExpression_LessThanOrEqual_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "le", "28");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Age <= 28));
    }

    [Fact]
    public void CreateSimplePropertyExpression_Between_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "between", "26,30");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Age >= 26 && e.Age <= 30));
    }

    [Fact]
    public void CreateSimplePropertyExpression_Between_ShouldThrowOnInvalidFormat()
    {
        // Arrange
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FilterOperatorParser.CreateSimplePropertyExpression(selector, "between", "25"));
    }

    [Fact]
    public void CreateSimplePropertyExpression_Contains_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "match", "oh");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(result);
        Assert.Contains("oh", result[0].Name);
    }

    [Fact]
    public void CreateSimplePropertyExpression_StartsWith_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "startswith", "Jo");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(result);
        Assert.StartsWith("Jo", result[0].Name);
    }

    [Fact]
    public void CreateSimplePropertyExpression_EndsWith_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "endswith", "ce");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(result);
        Assert.EndsWith("ce", result[0].Name);
    }

    [Fact]
    public void CreateSimplePropertyExpression_In_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "in", "John,Alice");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Name == "John");
        Assert.Contains(result, e => e.Name == "Alice");
    }

    [Fact]
    public void CreateSimplePropertyExpression_NotIn_ShouldFilterCorrectly()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "nin", "John,Alice");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("Jane", result[0].Name);
    }

    [Fact]
    public void CreateSimplePropertyExpression_ShouldHandleDecimalComparison()
    {
        // Arrange
        var entities = CreateTestEntities();
        Expression<Func<TestEntity, object>> selector = x => x.Price;

        // Act
        var expression = FilterOperatorParser.CreateSimplePropertyExpression(selector, "gt", "100");
        var result = entities.AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Price > 100m));
    }

    [Fact]
    public void CreateSimplePropertyExpression_ShouldThrowOnUnsupportedOperator()
    {
        // Arrange
        Expression<Func<TestEntity, object>> selector = x => x.Name;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FilterOperatorParser.CreateSimplePropertyExpression(selector, "unsupported", "value"));
    }

    [Fact]
    public void CreateSimplePropertyExpression_Contains_ShouldThrowOnNonStringProperty()
    {
        // Arrange
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FilterOperatorParser.CreateSimplePropertyExpression(selector, "match", "test"));
    }

    [Fact]
    public void CreateSimplePropertyExpression_StartsWith_ShouldThrowOnNonStringProperty()
    {
        // Arrange
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FilterOperatorParser.CreateSimplePropertyExpression(selector, "startswith", "test"));
    }

    [Fact]
    public void CreateSimplePropertyExpression_EndsWith_ShouldThrowOnNonStringProperty()
    {
        // Arrange
        Expression<Func<TestEntity, object>> selector = x => x.Age;

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FilterOperatorParser.CreateSimplePropertyExpression(selector, "endswith", "test"));
    }

    #endregion

    private static List<TestEntity> CreateTestEntities()
    {
        return new List<TestEntity>
        {
            new() { Id = 1, Name = "John", Age = 30, Price = 199.99m, CreatedAt = DateTime.UtcNow.AddDays(-5) },
            new() { Id = 2, Name = "Jane", Age = 28, Price = 150.00m, CreatedAt = DateTime.UtcNow.AddDays(-3) },
            new() { Id = 3, Name = "Alice", Age = 25, Price = 99.99m, CreatedAt = DateTime.UtcNow.AddDays(-1) }
        };
    }
}

