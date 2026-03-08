using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace System.Text.Json;

/// <summary>
/// Unit tests for ScriptContextExtensions
/// </summary>
public class ScriptContextExtensionsTests : DomainTestBase<DomainEntryPoint>
{
    #region GetBodyAsJsonElement Tests

    [Fact]
    public void GetBodyAsJsonElement_WithNullBody_ShouldReturnEmptyObject()
    {
        // Arrange
        var context = CreateContext(body: null);

        // Act
        var result = context.GetBodyAsJsonElement();

        // Assert
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public void GetBodyAsJsonElement_WithJsonElement_ShouldReturnSame()
    {
        // Arrange
        var jsonElement = JsonSerializer.Deserialize<JsonElement>("{\"key\":\"value\"}");
        var context = CreateContext(body: jsonElement);

        // Act
        var result = context.GetBodyAsJsonElement();

        // Assert
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("value", result.GetProperty("key").GetString());
    }

    [Fact]
    public void GetBodyAsJsonElement_WithComplexObject_ShouldSerializeAndDeserialize()
    {
        // Arrange
        var complexObject = new { Name = "test", Age = 30, Tags = new[] { "tag1", "tag2" } };
        var context = CreateContext(body: complexObject);

        // Act
        var result = context.GetBodyAsJsonElement();

        // Assert
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Equal("test", result.GetProperty("name").GetString());
        Assert.Equal(30, result.GetProperty("age").GetInt32());
    }

    #endregion

    #region GetHeadersAsDictionary Tests

    [Fact]
    public void GetHeadersAsDictionary_WithNullHeaders_ShouldReturnEmptyDictionary()
    {
        // Arrange
        var context = CreateContext(headers: null);

        // Act
        var result = context.GetHeadersAsDictionary();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetHeadersAsDictionary_WithDictionary_ShouldReturnSame()
    {
        // Arrange
        var headers = new Dictionary<string, string> { { "key1", "value1" }, { "key2", "value2" } };
        var context = CreateContext(headers: headers);

        // Act
        var result = context.GetHeadersAsDictionary();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("value1", result["key1"]);
        Assert.Equal("value2", result["key2"]);
    }

    [Fact]
    public void GetHeadersAsDictionary_WithValidJsonString_ShouldParse()
    {
        // Arrange
        var jsonString = "{\"Authorization\":\"Bearer token\",\"Content-Type\":\"application/json\"}";
        var context = CreateContext(headers: jsonString);

        // Act
        var result = context.GetHeadersAsDictionary();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("Bearer token", result["Authorization"]);
        Assert.Equal("application/json", result["Content-Type"]);
    }

    [Fact]
    public void GetHeadersAsDictionary_WithEmptyString_ShouldReturnEmptyDictionary()
    {
        // Arrange
        var context = CreateContext(headers: "");

        // Act
        var result = context.GetHeadersAsDictionary();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetHeadersAsDictionary_WithInvalidJsonString_ShouldReturnEmptyDictionary()
    {
        // Arrange
        var context = CreateContext(headers: "{invalid}");

        // Act
        var result = context.GetHeadersAsDictionary();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetHeadersAsDictionary_WithComplexObject_ShouldSerializeAndDeserialize()
    {
        // Arrange
        var headers = new { Key1 = "value1", Key2 = "value2" };
        var context = CreateContext(headers: headers);

        // Act
        var result = context.GetHeadersAsDictionary();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("key1") || result.ContainsKey("Key1"));
    }

    #endregion

    #region GetRouteValuesAsDictionary Tests

    [Fact]
    public void GetRouteValuesAsDictionary_WithNullRouteValues_ShouldReturnEmptyDictionary()
    {
        // Arrange
        var context = CreateContext(routeValues: null);

        // Act
        var result = context.GetRouteValuesAsDictionary();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetRouteValuesAsDictionary_WithDictionary_ShouldReturnSame()
    {
        // Arrange
        var routeValues = new Dictionary<string, string> { { "id", "123" }, { "domain", "test" } };
        var context = CreateContext(routeValues: routeValues);

        // Act
        var result = context.GetRouteValuesAsDictionary();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("123", result["id"]);
        Assert.Equal("test", result["domain"]);
    }

    [Fact]
    public void GetRouteValuesAsDictionary_WithValidJsonString_ShouldParse()
    {
        // Arrange
        var jsonString = "{\"id\":\"123\",\"action\":\"edit\"}";
        var context = CreateContext(routeValues: jsonString);

        // Act
        var result = context.GetRouteValuesAsDictionary();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Equal("123", result["id"]);
        Assert.Equal("edit", result["action"]);
    }

    [Fact]
    public void GetRouteValuesAsDictionary_WithEmptyString_ShouldReturnEmptyDictionary()
    {
        // Arrange
        var context = CreateContext(routeValues: "");

        // Act
        var result = context.GetRouteValuesAsDictionary();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void GetRouteValuesAsDictionary_WithInvalidJsonString_ShouldReturnEmptyDictionary()
    {
        // Arrange
        var context = CreateContext(routeValues: "{invalid}");

        // Act
        var result = context.GetRouteValuesAsDictionary();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    #endregion

    #region GetSafeProperties Tests

    [Fact]
    public void GetSafeProperties_ShouldReturnAllProperties()
    {
        // Arrange
        var body = JsonSerializer.Deserialize<JsonElement>("{\"key\":\"value\"}");
        var headers = new Dictionary<string, string> { { "h1", "v1" } };
        var routeValues = new Dictionary<string, string> { { "r1", "v1" } };
        var context = CreateContext(body: body, headers: headers, routeValues: routeValues);

        // Act
        var (resultBody, resultHeaders, resultRouteValues) = context.GetSafeProperties();

        // Assert
        Assert.Equal(JsonValueKind.Object, resultBody.ValueKind);
        Assert.Equal("value", resultBody.GetProperty("key").GetString());
        Assert.Single(resultHeaders);
        Assert.Equal("v1", resultHeaders["h1"]);
        Assert.Single(resultRouteValues);
        Assert.Equal("v1", resultRouteValues["r1"]);
    }

    [Fact]
    public void GetSafeProperties_WithNullValues_ShouldReturnEmptyStructures()
    {
        // Arrange
        var context = CreateContext(body: null, headers: null, routeValues: null);

        // Act
        var (resultBody, resultHeaders, resultRouteValues) = context.GetSafeProperties();

        // Assert
        Assert.Equal(JsonValueKind.Object, resultBody.ValueKind);
        Assert.Empty(resultHeaders);
        Assert.Empty(resultRouteValues);
    }

    [Fact]
    public void GetSafeProperties_WithComplexData_ShouldHandleCorrectly()
    {
        // Arrange
        var body = new { Valid = "data", Count = 123 };
        var headers = new Dictionary<string, string> { { "h1", "v1" } };
        var routeValues = new Dictionary<string, string> { { "r1", "v1" } };
        var context = CreateContext(body: body, headers: headers, routeValues: routeValues);

        // Act
        var (resultBody, resultHeaders, resultRouteValues) = context.GetSafeProperties();

        // Assert
        Assert.Equal(JsonValueKind.Object, resultBody.ValueKind);
        Assert.Single(resultHeaders);
        Assert.Single(resultRouteValues);
    }

    #endregion

    #region Helper Methods

    private ScriptContext CreateContext(
        object? body = null,
        object? headers = null,
        object? routeValues = null)
    {
        var workflow = Workflow.Create();
        var instance = Instance.Create(Guid.NewGuid(), "test-flow","1.0.0", "test-key");

        return new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetWorkflow(workflow)
            .SetInstance(instance)
            .SetBody(body)
            .SetHeaders(headers)
            .SetRouteValues(routeValues)
            .Build();
    }

    #endregion
}

