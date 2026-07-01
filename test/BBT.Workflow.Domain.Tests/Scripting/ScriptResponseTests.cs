using System;
using System.Dynamic;
using BBT.Workflow.Scripting;
using Xunit;

namespace BBT.Workflow.Scripting;

public class ScriptResponseTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithDefaultValues()
    {
        // Arrange & Act
        var response = new ScriptResponse();

        // Assert
        Assert.Null(response.Key);
        Assert.Null(response.Data);
        Assert.Null(response.Headers);
        Assert.Null(response.StatusCode);
        Assert.Null(response.RouteValues);
        Assert.NotNull(response.Tags);
        Assert.Empty(response.Tags);
    }

    [Fact]
    public void StatusCode_ShouldBeSettable()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.StatusCode = 400;

        // Assert
        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public void Key_ShouldBeSettable()
    {
        // Arrange
        var response = new ScriptResponse();
        var key = "test-key";

        // Act
        response.Key = key;

        // Assert
        Assert.Equal(key, response.Key);
    }

    [Fact]
    public void Data_ShouldBeSettable()
    {
        // Arrange
        var response = new ScriptResponse();
        dynamic data = new ExpandoObject();
        data.Name = "Test";
        data.Value = 123;

        // Act
        response.Data = data;

        // Assert
        Assert.NotNull(response.Data);
        Assert.Equal("Test", response.Data.Name);
        Assert.Equal(123, response.Data.Value);
    }

    [Fact]
    public void Data_ShouldAcceptDifferentTypes()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act & Assert - String
        response.Data = "string data";
        Assert.Equal("string data", response.Data);

        // Act & Assert - Integer
        response.Data = 42;
        Assert.Equal(42, response.Data);

        // Act & Assert - Object
        response.Data = new { Id = 1, Name = "Test" };
        Assert.NotNull(response.Data);
    }

    [Fact]
    public void Headers_ShouldBeSettable()
    {
        // Arrange
        var response = new ScriptResponse();
        dynamic headers = new ExpandoObject();
        headers.ContentType = "application/json";
        headers.Authorization = "Bearer token";

        // Act
        response.Headers = headers;

        // Assert
        Assert.NotNull(response.Headers);
        Assert.Equal("application/json", response.Headers.ContentType);
        Assert.Equal("Bearer token", response.Headers.Authorization);
    }

    [Fact]
    public void RouteValues_ShouldBeSettable()
    {
        // Arrange
        var response = new ScriptResponse();
        dynamic routeValues = new ExpandoObject();
        routeValues.Controller = "Workflow";
        routeValues.Action = "Execute";

        // Act
        response.RouteValues = routeValues;

        // Assert
        Assert.NotNull(response.RouteValues);
        Assert.Equal("Workflow", response.RouteValues.Controller);
        Assert.Equal("Execute", response.RouteValues.Action);
    }

    [Fact]
    public void Tags_ShouldBeSettable()
    {
        // Arrange
        var response = new ScriptResponse();
        var tags = new[] { "tag1", "tag2", "tag3" };

        // Act
        response.Tags = tags;

        // Assert
        Assert.Equal(3, response.Tags.Length);
        Assert.Contains("tag1", response.Tags);
        Assert.Contains("tag2", response.Tags);
        Assert.Contains("tag3", response.Tags);
    }

    [Fact]
    public void Tags_ShouldHandleEmptyArray()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.Tags = Array.Empty<string>();

        // Assert
        Assert.NotNull(response.Tags);
        Assert.Empty(response.Tags);
    }

    [Fact]
    public void AllProperties_ShouldBeSetSimultaneously()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.Key = "workflow-123";
        dynamic data = new ExpandoObject();
        data.Result = "Success";
        response.Data = data;
        
        dynamic headers = new ExpandoObject();
        headers.ContentType = "application/json";
        response.Headers = headers;
        
        dynamic routeValues = new ExpandoObject();
        routeValues.Id = "123";
        response.RouteValues = routeValues;
        
        response.Tags = new[] { "audit", "important" };

        // Assert
        Assert.Equal("workflow-123", response.Key);
        Assert.Equal("Success", response.Data.Result);
        Assert.Equal("application/json", response.Headers.ContentType);
        Assert.Equal("123", response.RouteValues.Id);
        Assert.Equal(2, response.Tags.Length);
    }

    [Fact]
    public void Data_ShouldAcceptNullValue()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.Data = null;

        // Assert
        Assert.Null(response.Data);
    }

    [Fact]
    public void Headers_ShouldAcceptNullValue()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.Headers = null;

        // Assert
        Assert.Null(response.Headers);
    }

    [Fact]
    public void RouteValues_ShouldAcceptNullValue()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.RouteValues = null;

        // Assert
        Assert.Null(response.RouteValues);
    }

    [Fact]
    public void Key_ShouldAcceptNullValue()
    {
        // Arrange
        var response = new ScriptResponse();

        // Act
        response.Key = null;

        // Assert
        Assert.Null(response.Key);
    }

    [Fact]
    public void Response_ShouldSupportComplexDataStructures()
    {
        // Arrange
        var response = new ScriptResponse();
        dynamic complexData = new ExpandoObject();
        complexData.User = new { Id = 1, Name = "John" };
        complexData.Metadata = new { Timestamp = DateTime.UtcNow, Version = "1.0" };
        complexData.Items = new[] { "item1", "item2", "item3" };

        // Act
        response.Data = complexData;

        // Assert
        Assert.NotNull(response.Data);
        Assert.NotNull(response.Data.User);
        Assert.NotNull(response.Data.Metadata);
        Assert.NotNull(response.Data.Items);
    }
}

