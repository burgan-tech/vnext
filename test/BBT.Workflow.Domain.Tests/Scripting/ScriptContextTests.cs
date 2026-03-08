using System;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Moq;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Scripting;

public class ScriptContextTests
{
    [Fact]
    public void Builder_ShouldCreateScriptContext()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder.Build();

        // Assert
        Assert.NotNull(context);
    }

    [Fact]
    public void Builder_SetBody_ShouldSetBodyProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var body = new { Name = "Test", Value = 123 };

        // Act
        var context = builder
            .SetBody(body)
            .Build();

        // Assert
        Assert.NotNull(context.Body);
    }

    [Fact]
    public void Builder_SetHeaders_ShouldSetHeadersProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var headers = new { ContentType = "application/json" };

        // Act
        var context = builder
            .SetHeaders(headers)
            .Build();

        // Assert
        Assert.NotNull(context.Headers);
    }

    [Fact]
    public void Builder_SetRouteValues_ShouldSetRouteValuesProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var routeValues = new { Controller = "Workflow", Action = "Execute" };

        // Act
        var context = builder
            .SetRouteValues(routeValues)
            .Build();

        // Assert
        Assert.NotNull(context.RouteValues);
    }

    [Fact]
    public void Builder_SetWorkflow_ShouldSetWorkflowProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var workflow = Definitions.Workflow.Create();

        // Act
        var context = builder
            .SetWorkflow(workflow)
            .Build();

        // Assert
        Assert.NotNull(context.Workflow);
        Assert.Same(workflow, context.Workflow);
    }

    [Fact]
    public void Builder_SetInstance_ShouldSetInstanceProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var instance = Instance.Create(
            Guid.NewGuid(),
            "test-flow",
            "1.0.0",
            "test-key");

        // Act
        var context = builder
            .SetInstance(instance)
            .Build();

        // Assert
        Assert.NotNull(context.Instance);
        Assert.Same(instance, context.Instance);
    }

    [Fact]
    public void Builder_SetTransition_ShouldSetTransitionProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var transition = Transition.Create("test-transition", null, "target-state", TriggerType.Manual, "Patch");

        // Act
        var context = builder
            .SetTransition(transition)
            .Build();

        // Assert
        Assert.NotNull(context.Transition);
        Assert.Same(transition, context.Transition);
    }

    [Fact]
    public void Builder_SetTransition_ShouldHandleNullTransition()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder
            .SetTransition(null)
            .Build();

        // Assert
        // Should not throw and context should be created
        Assert.NotNull(context);
    }

    [Fact]
    public void Builder_SetRuntime_ShouldSetRuntimeProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var runtime = Substitute.For<IRuntimeInfoProvider>();

        // Act
        var context = builder
            .SetRuntime(runtime)
            .Build();

        // Assert
        Assert.NotNull(context.Runtime);
        Assert.Same(runtime, context.Runtime);
    }

    [Fact]
    public void Builder_SetDefinitions_ShouldSetDefinitionsProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var definitions = new Dictionary<string, object>
        {
            { "def1", "value1" },
            { "def2", 123 }
        };

        // Act
        var context = builder
            .SetDefinitions(definitions)
            .Build();

        // Assert
        Assert.NotNull(context.Definitions);
        Assert.Equal(2, context.Definitions.Count);
    }

    [Fact]
    public void Builder_SetTaskResponse_ShouldSetTaskResponseProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var taskResponse = new Dictionary<string, object?>
        {
            { "task1", new { Result = "Success" } }
        };

        // Act
        var context = builder
            .SetTaskResponse(taskResponse)
            .Build();

        // Assert
        Assert.NotNull(context.TaskResponse);
        Assert.Single(context.TaskResponse);
    }

    [Fact]
    public void Builder_SetMetadata_ShouldSetMetadataProperty()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var metadata = new Dictionary<string, object>
        {
            { "key1", "value1" },
            { "key2", 456 }
        };

        // Act
        var context = builder
            .SetMetadata(metadata)
            .Build();

        // Assert
        Assert.NotNull(context.MetaData);
        Assert.Equal(2, context.MetaData.Count);
    }

    [Fact]
    public void Builder_ShouldSupportMethodChaining()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var workflow = Definitions.Workflow.Create();
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0","test-key");
        var runtime = Substitute.For<IRuntimeInfoProvider>();

        // Act
        var context = builder
            .SetBody(new { Test = "Data" })
            .SetHeaders(new { Auth = "Bearer token" })
            .SetRouteValues(new { Id = 123 })
            .SetWorkflow(workflow)
            .SetInstance(instance)
            .SetRuntime(runtime)
            .Build();

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(context.Body);
        Assert.NotNull(context.Headers);
        Assert.NotNull(context.RouteValues);
        Assert.NotNull(context.Workflow);
        Assert.NotNull(context.Instance);
        Assert.NotNull(context.Runtime);
    }

    [Fact]
    public void SetBody_ShouldMergeWithExistingBody()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var initialBody = new { Name = "Initial" };
        var additionalData = new { Value = 123 };

        // Act
        var context = builder
            .SetBody(initialBody)
            .Build();
        
        context.SetBody(additionalData);

        // Assert
        Assert.NotNull(context.Body);
        // Both properties should exist after merge
        Assert.Equal("Initial", context.Body?.name);
        Assert.Equal(123, context.Body?.value);
    }

    [Fact]
    public void SetStandardResponse_ShouldSetBodyWithStandardTaskResponse()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder.Build();
        var response = new StandardTaskResponse
        {
            Data = new { Result = "Success" },
            StatusCode = 200,
            IsSuccess = true
        };

        // Act
        context.SetStandardResponse(response, "taskKey");

        // Assert
        Assert.NotNull(context.Body);
    }

    [Fact]
    public void Dispose_ShouldClearCollections()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder
            .SetTaskResponse(new Dictionary<string, object?> { { "task1", "value" } })
            .SetMetadata(new Dictionary<string, object> { { "key", "value" } })
            .SetDefinitions(new Dictionary<string, object> { { "def", "value" } })
            .Build();

        // Act
        context.Dispose();

        // Assert
        // Collections should be cleared
        Assert.Empty(context.TaskResponse);
        Assert.Empty(context.MetaData);
        Assert.Empty(context.Definitions);
    }

    [Fact]
    public void Dispose_ShouldClearDynamicObjects()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder
            .SetBody(new { Test = "Data" })
            .SetHeaders(new { Auth = "Token" })
            .SetRouteValues(new { Id = 1 })
            .Build();

        // Act
        context.Dispose();

        // Assert
        Assert.Null(context.Body);
        Assert.Null(context.Headers);
        Assert.Null(context.RouteValues);
    }

    [Fact]
    public void Dispose_ShouldBeIdempotent()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder.Build();

        // Act & Assert - Should not throw
        context.Dispose();
        context.Dispose();
        context.Dispose();
    }

    [Fact]
    public void SetBody_ShouldThrowWhenDisposed()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder.Build();
        context.Dispose();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => context.SetBody(new { Test = "Data" }));
    }

    [Fact]
    public void SetStandardResponse_ShouldThrowWhenDisposed()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder.Build();
        context.Dispose();
        var response = new StandardTaskResponse();

        // Act & Assert
        Assert.Throws<ObjectDisposedException>(() => context.SetStandardResponse(response, "taskKey"));
    }

    [Fact]
    public void SetBody_ShouldHandleNullValue()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder
            .SetBody(new { Test = "Data" })
            .Build();

        // Act
        context.SetBody(null);

        // Assert - Body should remain unchanged when null is passed
        Assert.NotNull(context.Body);
    }

    [Fact]
    public void TaskResponse_ShouldBeInitialized()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder.Build();

        // Assert
        Assert.NotNull(context.TaskResponse);
        Assert.Empty(context.TaskResponse);
    }

    [Fact]
    public void MetaData_ShouldBeInitialized()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder.Build();

        // Assert
        Assert.NotNull(context.MetaData);
        Assert.Empty(context.MetaData);
    }

    [Fact]
    public void SetBody_ShouldSerializeAndDeserializeCorrectly()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());
        var context = builder.Build();
        var complexObject = new
        {
            Name = "Test",
            Value = 123,
            Nested = new { Inner = "Data" },
            Array = new[] { 1, 2, 3 }
        };

        // Act
        context.SetBody(complexObject);

        // Assert
        Assert.NotNull(context.Body);
        Assert.Equal("Test", context.Body?.name); // camelCase
        Assert.Equal(123, context.Body?.value);
        Assert.NotNull(context.Body?.nested);
        Assert.NotNull(context.Body?.array);
    }

    [Fact]
    public void Builder_SetBody_ShouldSupportNullValue()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder
            .SetBody(null)
            .Build();

        // Assert
        Assert.Null(context.Body);
    }

    [Fact]
    public void Builder_SetHeaders_ShouldSupportNullValue()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder
            .SetHeaders(null)
            .Build();

        // Assert
        Assert.Null(context.Headers);
    }

    [Fact]
    public void Builder_SetRouteValues_ShouldSupportNullValue()
    {
        // Arrange
        var builder = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>());

        // Act
        var context = builder
            .SetRouteValues(null)
            .Build();

        // Assert
        Assert.Null(context.RouteValues);
    }

    [Fact]
    public void JsonScriptBodyOptions_ShouldBeCamelCase()
    {
        // Arrange
        var testObject = new { FirstName = "John", LastName = "Doe" };

        // Act
        var json = JsonSerializer.Serialize(testObject, ScriptContext.JsonScriptBodyOptions);

        // Assert
        Assert.Contains("firstName", json);
        Assert.Contains("lastName", json);
        Assert.DoesNotContain("FirstName", json);
        Assert.DoesNotContain("LastName", json);
    }
}

