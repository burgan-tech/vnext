using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
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
public class ConfigurationFunctionsTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly IScriptEngine _scriptEngine;
    private Mock<IConfiguration>? _mockConfiguration;

    public ConfigurationFunctionsTests()
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
        var mockLogger = new Mock<ILogger<ScriptServices>>();
        mockLogger
            .Setup(x => x.Log(
                It.IsAny<LogLevel>(),
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()));

        services.AddSingleton(mockLogger.Object);

        // Mock Configuration
        _mockConfiguration = new Mock<IConfiguration>();
        _mockConfiguration
            .Setup(x => x[It.IsAny<string>()])
            .Returns((string key) =>
            {
                return key switch
                {
                    "TestKey" => "TestValue",
                    "AppSettings:ApiUrl" => "https://api.example.com",
                    "AppSettings:Timeout" => "30",
                    _ => null
                };
            });

        _mockConfiguration
            .Setup(x => x.GetSection(It.IsAny<string>()))
            .Returns((string key) => Mock.Of<IConfigurationSection>());

        services.AddSingleton(_mockConfiguration.Object);

        // Mock IWorkflowMetrics
        var mockWorkflowMetrics = new Mock<IWorkflowMetrics>();
        services.AddSingleton(mockWorkflowMetrics.Object);

        base.AddApplication(services);
    }

    [Fact]
    public async Task GetConfigValue_Should_Return_Configuration_Value()
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
                           var configValue = GetConfigValue("TestKey");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = configValue,
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
        Assert.Equal("TestValue", response.Data);
        _mockConfiguration!.Verify(x => x["TestKey"], Times.Once);
    }

    [Fact]
    public async Task GetConfigValue_With_DefaultValue_Should_Return_DefaultValue_When_Key_Not_Found()
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
                           var configValue = GetConfigValue("NonExistentKey", "DefaultValue");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = configValue,
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
        Assert.Equal("DefaultValue", response.Data);
    }

    [Fact]
    public async Task GetConfigValue_With_Nested_Key_Should_Return_Configuration_Value()
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
                           var apiUrl = GetConfigValue("AppSettings:ApiUrl");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = apiUrl,
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
        Assert.Equal("https://api.example.com", response.Data);
        _mockConfiguration!.Verify(x => x["AppSettings:ApiUrl"], Times.Once);
    }

    [Fact]
    public async Task ConfigExists_Should_Return_True_When_Key_Exists()
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
                           var exists = ConfigExists("TestKey");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = exists ? "Key exists" : "Key not found",
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
        Assert.Equal("Key exists", response.Data);
    }

    [Fact]
    public async Task ConfigExists_Should_Return_False_When_Key_Not_Exists()
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
                           var exists = ConfigExists("NonExistentKey");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = exists ? "Key exists" : "Key not found",
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
        Assert.Equal("Key not found", response.Data);
    }
}
