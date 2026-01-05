using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Functions;
using Dapr.Client;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BBT.Workflow.Scripting;

[Collection("ScriptingTests")]
public class LoggingFunctionsTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly IScriptEngine _scriptEngine;
    private Mock<ILogger<ScriptServices>>? _mockLogger;

    public LoggingFunctionsTests()
    {
        _scriptEngine = GetRequiredService<IScriptEngine>();
    }

    protected override void AddApplication(IServiceCollection services)
    {
        // Mock DaprClient
        var mockDaprClient = new Mock<DaprClient>();
        mockDaprClient
            .Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { { "test_key", "mock_secret_value" } });

        services.AddSingleton(mockDaprClient.Object);

        // Mock Logger for IScriptServices
        _mockLogger = new Mock<ILogger<ScriptServices>>();
        _mockLogger
            .Setup(x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()));

        services.AddSingleton(_mockLogger.Object);

        // Mock Configuration
        var mockConfiguration = new Mock<IConfiguration>();
        mockConfiguration
            .Setup(x => x[It.IsAny<string>()])
            .Returns((string key) => key == "TestKey" ? "TestValue" : null);

        services.AddSingleton(mockConfiguration.Object);

        // Mock IWorkflowMetrics
        var mockWorkflowMetrics = new Mock<IWorkflowMetrics>();
        services.AddSingleton(mockWorkflowMetrics.Object);

        base.AddApplication(services);
    }

    [Fact]
    public async Task LogTrace_Should_Call_Logger_With_Trace_Level()
    {
        // Arrange
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class TestMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           LogTrace("This is a trace message");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Trace logged",
                               Headers = null
                           });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Output",
                               Headers = null
                           });
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var usings = new[]
        {
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions",
            "BBT.Workflow.Scripting.Functions"
        };

        // Act
        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references, usings);

        var httpTask = WorkflowTaskFactory.CreateHttpTask();
        var response = await instance.InputHandler(
            task: httpTask,
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Trace logged", response.Data);
        _mockLogger!.Verify(
            x => x.Log(
                LogLevel.Trace,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogInformation_Should_Call_Logger_With_Information_Level()
    {
        // Arrange
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class TestMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           LogInformation("This is an info message");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Info logged",
                               Headers = null
                           });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Output",
                               Headers = null
                           });
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var usings = new[]
        {
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions",
            "BBT.Workflow.Scripting.Functions"
        };

        // Act
        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references, usings);

        var httpTask = WorkflowTaskFactory.CreateHttpTask();
        var response = await instance.InputHandler(
            task: httpTask,
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Info logged", response.Data);
        _mockLogger!.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogWarning_Should_Call_Logger_With_Warning_Level()
    {
        // Arrange
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class TestMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           LogWarning("This is a warning message");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Warning logged",
                               Headers = null
                           });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Output",
                               Headers = null
                           });
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var usings = new[]
        {
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions",
            "BBT.Workflow.Scripting.Functions"
        };

        // Act
        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references, usings);

        var httpTask = WorkflowTaskFactory.CreateHttpTask();
        var response = await instance.InputHandler(
            task: httpTask,
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Warning logged", response.Data);
        _mockLogger!.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogError_Should_Call_Logger_With_Error_Level()
    {
        // Arrange
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class TestMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           LogError("This is an error message");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Error logged",
                               Headers = null
                           });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Output",
                               Headers = null
                           });
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var usings = new[]
        {
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions",
            "BBT.Workflow.Scripting.Functions"
        };

        // Act
        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references, usings);

        var httpTask = WorkflowTaskFactory.CreateHttpTask();
        var response = await instance.InputHandler(
            task: httpTask,
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Error logged", response.Data);
        _mockLogger!.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task LogCritical_Should_Call_Logger_With_Critical_Level()
    {
        // Arrange
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class TestMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           LogCritical("This is a critical message");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Critical logged",
                               Headers = null
                           });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Output",
                               Headers = null
                           });
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var usings = new[]
        {
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions",
            "BBT.Workflow.Scripting.Functions"
        };

        // Act
        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references, usings);

        var httpTask = WorkflowTaskFactory.CreateHttpTask();
        var response = await instance.InputHandler(
            task: httpTask,
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Critical logged", response.Data);
        _mockLogger!.Verify(
            x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
