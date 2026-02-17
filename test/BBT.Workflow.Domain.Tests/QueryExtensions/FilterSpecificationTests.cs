using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Domain.Tests.QueryExtensions;

/// <summary>
/// Unit tests for FilterSpecification
/// </summary>
public class FilterSpecificationTests : DomainTestBase<DomainEntryPoint>
{
    private class TestEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public bool IsActive { get; set; }
    }

    [Fact]
    public void FilterSpecification_ShouldReturnTrueExpression_WhenNoFilters()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>(null, filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.NotNull(expression);
        Assert.Equal(3, entities.Count);
    }

    [Fact]
    public void FilterSpecification_ShouldReturnTrueExpression_WhenEmptyFilters()
    {
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.NotNull(expression);
        Assert.Equal(3, entities.Count);
    }

    [Fact]
    public void FilterSpecification_ShouldApplyJsonFilter()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var jsonFilter = JsonSerializer.Serialize(new { Name = "John" });
        var spec = new FilterSpecification<TestEntity>(jsonFilter, filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("John", entities[0].Name);
    }

    [Fact]
    public void FilterSpecification_ShouldApplyKeyValueFilter()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("Name=Jane", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("Jane", entities[0].Name);
    }

    [Fact]
    public void FilterSpecification_ShouldCombineMultipleFiltersWithAnd()
    {
        var filterMappings = CreateFilterMappings();
        var jsonFilter = JsonSerializer.Serialize(new { Name = "John", Age = 30 });
        var spec = new FilterSpecification<TestEntity>(jsonFilter, filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("John", entities[0].Name);
        Assert.Equal(30, entities[0].Age);
    }

    [Fact]
    public void FilterSpecification_ShouldHandleInvalidFilter()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("InvalidFormat", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert - should return all entities when filter is invalid
        Assert.Equal(3, entities.Count);
    }

    [Fact]
    public void FilterSpecification_ShouldIgnoreUnmappedProperty()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("UnmappedProperty=value", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert - should return all entities when property is not mapped
        Assert.Equal(3, entities.Count);
    }

    [Fact]
    public void FilterSpecification_ShouldApplyJsonFilterWithMultipleProperties()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var jsonFilter = JsonSerializer.Serialize(new { Name = "Alice", Age = 25 });
        var spec = new FilterSpecification<TestEntity>(jsonFilter, filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("Alice", entities[0].Name);
        Assert.Equal(25, entities[0].Age);
    }

    [Fact]
    public void FilterSpecification_ShouldHandleCaseInsensitivePropertyNames()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("name=John", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("John", entities[0].Name);
    }

    [Fact]
    public void FilterSpecification_ShouldHandleWhitespaceInFilter()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("  Name  =  Jane  ", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("Jane", entities[0].Name);
    }

    [Fact]
    public void FilterSpecification_Apply_ShouldFilterQuery()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("Name=John", filterMappings);
        var query = CreateTestEntities().AsQueryable();

        // Act
        var result = spec.Apply(query).ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("John", result[0].Name);
    }

    [Fact]
    public void FilterSpecification_ShouldHandleMixedJsonAndKeyValueFilters()
    {
        var filterMappings = CreateFilterMappings();
        var jsonFilter = JsonSerializer.Serialize(new { Name = "Alice", Age = 25 });
        var spec = new FilterSpecification<TestEntity>(jsonFilter, filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal("Alice", entities[0].Name);
        Assert.Equal(25, entities[0].Age);
    }

    [Fact]
    public void FilterSpecification_ShouldHandleBooleanFilter()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("IsActive=true", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Equal(2, entities.Count);
        Assert.All(entities, e => Assert.True(e.IsActive));
    }

    [Fact]
    public void FilterSpecification_ShouldHandleIntegerFilter()
    {
        // Arrange
        var filterMappings = CreateFilterMappings();
        var spec = new FilterSpecification<TestEntity>("Age=30", filterMappings);

        // Act
        var expression = spec.ToExpression();
        var entities = CreateTestEntities().AsQueryable().Where(expression).ToList();

        // Assert
        Assert.Single(entities);
        Assert.Equal(30, entities[0].Age);
    }

    private static List<TestEntity> CreateTestEntities()
    {
        return new List<TestEntity>
        {
            new() { Id = 1, Name = "John", Age = 30, IsActive = true },
            new() { Id = 2, Name = "Jane", Age = 28, IsActive = false },
            new() { Id = 3, Name = "Alice", Age = 25, IsActive = true }
        };
    }

    private static Dictionary<string, Func<string, Expression<Func<TestEntity, bool>>>> CreateFilterMappings()
    {
        return new Dictionary<string, Func<string, Expression<Func<TestEntity, bool>>>>
        {
            ["Name"] = value => x => x.Name == value,
            ["Age"] = value => x => x.Age == int.Parse(value),
            ["IsActive"] = value => x => x.IsActive == bool.Parse(value)
        };
    }
}

