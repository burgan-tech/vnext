using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Scripting.Functions;
using Microsoft.CodeAnalysis;
using Moq;
using Xunit;
using IRuntimeInfoProvider = BBT.Workflow.Runtime.IRuntimeInfoProvider;
using Dapr.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Scripting;

[Collection("ScriptingTests")]
public class ScriptEngineTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly IScriptEngine _scriptEngine;

    public ScriptEngineTests()
    {
        _scriptEngine = GetRequiredService<IScriptEngine>();
    }

    protected override void AddApplication(IServiceCollection services)
    {
        // Mock DaprClient for testing
        var mockDaprClient = new Mock<DaprClient>();
        
        // Setup GetSecretAsync to return a mock secret value
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
        services.AddSingleton(mockLogger.Object);
        
        // Mock Configuration for IScriptServices
        var mockConfiguration = new Mock<IConfiguration>();
        services.AddSingleton(mockConfiguration.Object);
        
        // Mock IWorkflowMetrics
        var mockWorkflowMetrics = new Mock<IWorkflowMetrics>();
        services.AddSingleton(mockWorkflowMetrics.Object);
        
        base.AddApplication(services);
    }

    [Fact]
    public async Task CompileToInstanceAsync_ShouldReturnCompiledInstance()
    {
        // Arrange
        string code = @"
            public class MyCompiledClass : IMyCompiledClass
            {
                public string SayHello() => ""Hello from compiled!"";
            }
        ";
        // Act
        var result = await _scriptEngine.CompileToInstanceAsync<IMyCompiledClass>(code,
            extraReferences:
            [
                MetadataReference.CreateFromFile(typeof(IMyCompiledClass).Assembly.Location)
            ],
            usingDirectives:
            [
                "System",
                "BBT.Workflow.Scripting"
            ]);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Hello from compiled!", result.SayHello());
    }

    [Fact]
    public async Task Compile_IMapping_From_Code_Should_Work()
    {
        // Arrange
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;

                   public class MockMapping : IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           var httpTask = (task as HttpTask)!;
                           httpTask.Url = "https://httpbin.org/post/" + context.Transition.Key;
                           httpTask.Method = "POST";
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Hello Input",
                               Headers = null
                           });
                       }
                   
                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Hello Output",
                               Headers = null
                           });
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location)
        };

        var usings = new[]
        {
            "System.Threading.Tasks",
            "BBT.Workflow.Scripting",
            "BBT.Workflow.Definitions"
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
        Assert.Equal("Hello Input", response.Data);
        Assert.Equal("POST", httpTask.Method);
        Assert.Equal("https://httpbin.org/post/test-transition", httpTask.Url);
    }

    [Fact]
    public async Task Compile_IMapping_With_ScriptBase_Should_Work_With_MockedDaprClient()
    {
        // Arrange - ScriptBase now uses injected IScriptServices
        var code = """
                   using System.Collections.Generic;
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class ScriptBaseMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           var httpTask = (task as HttpTask)!;
                           httpTask.Url = "https://httpbin.org/post/" + context.Transition.Key;
                           httpTask.Method = "POST";
                          
                           var apiKey = GetSecret("secret_store", "secret", "test_key");
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Got secret: " + apiKey,
                               Headers = null
                           });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                       {
                           return Task.FromResult(new ScriptResponse
                           {
                               Data = "Hello Output from ScriptBase",
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
            "System.Collections.Generic",
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
        Assert.Equal("Got secret: mock_secret_value", response.Data);
    }

}

public interface IMyCompiledClass
{
    string SayHello();
}
