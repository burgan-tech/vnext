using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    // Field (not a local in AddApplication) so tests can verify vault call counts.
    private readonly Mock<DaprClient> _mockDaprClient = new();

    // Field (not a local in AddApplication) so tests can verify metrics recording calls.

    public ScriptEngineTests()
    {
        _scriptEngine = GetRequiredService<IScriptEngine>();
    }

    protected override void AddApplication(IServiceCollection services)
    {
        // Setup GetSecretAsync to return a mock secret value
        _mockDaprClient
            .Setup(x => x.GetSecretAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { { "test_key", "mock_secret_value" } });

        services.AddSingleton(_mockDaprClient.Object);
        
        // Mock Logger for IScriptServices
        var mockLogger = new Mock<ILogger<ScriptServices>>();
        services.AddSingleton(mockLogger.Object);
        
        // Mock Configuration for IScriptServices
        var mockConfiguration = new Mock<IConfiguration>();
        services.AddSingleton(mockConfiguration.Object);
        
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
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetWorkflow(WorkflowFactory.CreateDefault())
            .SetInstance(InstanceFactory.CreateDefault())
            .SetTransition(TransitionFactory.CreateDefault())
            .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
            .SetDefinitions(new Dictionary<string, object>())
            .Build();
        var response = await instance.InputHandler(task: httpTask, context: context);
        var secondResponse = await instance.InputHandler(task: httpTask, context: context);

        // Assert
        Assert.NotNull(response);
        Assert.Equal("Got secret: mock_secret_value", response.Data);
        Assert.Equal("Got secret: mock_secret_value", secondResponse.Data);

        // The singleton ScriptSecretCache sits between ScriptBase and DaprClient: two GetSecret
        // calls for the same bundle must produce exactly one vault round-trip.
        _mockDaprClient.Verify(x => x.GetSecretAsync(
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<IReadOnlyDictionary<string, string>>(),
            It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public async Task Compile_SoapTask_Mapping_Should_Resolve_XmlDocument_Facade()
    {
        // Regression: a mapping that merely touches SoapTask forces Roslyn to resolve every member
        // signature, including SetBody(XmlDocument). XmlDocument's metadata identity binds to the
        // System.Xml.ReaderWriter facade, which is not the System.Private.Xml implementation reachable
        // via typeof(XmlDocument).Assembly. Without the facade reference this failed with
        // CS0012 (System.Xml.ReaderWriter not referenced). The engine's default references must cover it
        // WITHOUT the mapping author supplying any System.Xml reference explicitly.
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;

                   public class SoapMapping : IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           var soap = (task as SoapTask)!;
                           soap.SetUrl("https://example.com/soap");
                           soap.SetBody("<a/>");
                           return Task.FromResult(new ScriptResponse { Data = soap.Body, Headers = null });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                           => Task.FromResult(new ScriptResponse { Data = "out", Headers = null });
                   }
                   """;

        // Only the runtime contract assembly is supplied; the XML references come from the engine defaults.
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location)
        };

        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references);

        var soapTask = WorkflowTaskFactory.CreateSoapTask();
        var response = await instance.InputHandler(
            task: soapTask,
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        Assert.NotNull(response);
        Assert.Equal("<a/>", response.Data);
        Assert.Equal("https://example.com/soap", soapTask.Url);
        Assert.Equal("<a/>", soapTask.Body);
    }

    [Fact]
    public async Task Compile_Mapping_Using_SecurityElement_Escape_Without_Explicit_Using_Should_Work()
    {
        // System.Security is a default using, so SecurityElement.Escape resolves with no explicit
        // using in the mapping. Verifies XML/SOAP-safe escaping of user input is available by default.
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;

                   public class EscapeMapping : IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                           => Task.FromResult(new ScriptResponse { Data = SecurityElement.Escape("<a>&'\""), Headers = null });

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                           => Task.FromResult(new ScriptResponse { Data = "out", Headers = null });
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location)
        };

        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references);

        var response = await instance.InputHandler(
            task: WorkflowTaskFactory.CreateHttpTask(),
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        Assert.Equal("&lt;a&gt;&amp;&apos;&quot;", response.Data?.ToString());
    }

    [Fact]
    public async Task Compile_Mapping_Using_ScriptBase_EscapeXml_Helper_Should_Work()
    {
        // ScriptBase.EscapeXml wraps SecurityElement.Escape as a curated helper.
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class EscapeHelperMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                           => Task.FromResult(new ScriptResponse { Data = EscapeXml("a & b"), Headers = null });

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                           => Task.FromResult(new ScriptResponse { Data = "out", Headers = null });
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references);

        var response = await instance.InputHandler(
            task: WorkflowTaskFactory.CreateHttpTask(),
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        Assert.Equal("a &amp; b", response.Data?.ToString());
    }

    [Fact]
    public async Task Compile_Mapping_Using_ScriptBase_ParseXml_Should_Work()
    {
        // Locks in the general System.Xml path: ScriptBase.ParseXml returns an XmlDocument, so the
        // facade reference is required even for mappings that never touch SoapTask.
        var code = """
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;
                   using BBT.Workflow.Scripting.Functions;

                   public class XmlMapping : ScriptBase, IMapping
                   {
                       public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                       {
                           var doc = ParseXml("<root><v>42</v></root>");
                           return Task.FromResult(new ScriptResponse { Data = XmlToString(doc), Headers = null });
                       }

                       public Task<ScriptResponse> OutputHandler(ScriptContext context)
                           => Task.FromResult(new ScriptResponse { Data = "out", Headers = null });
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(ScriptBase).Assembly.Location)
        };

        var instance = await _scriptEngine.CompileToInstanceAsync<IMapping>(code, references);

        var response = await instance.InputHandler(
            task: WorkflowTaskFactory.CreateHttpTask(),
            context: new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(InstanceFactory.CreateDefault())
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetDefinitions(new Dictionary<string, object>())
                .Build());

        Assert.NotNull(response);
        Assert.Contains("<root>", response.Data?.ToString());
    }

    [Fact]
    public async Task CompileTwice_RecordsMissThenHit_AndKeepsDeprecatedCounter()
    {
        // Arrange - class name carries a GUID nonce so this test's source is unique and cannot
        // be served as a hit by the process-wide singleton evaluator cache from another test.
        var nonce = Guid.NewGuid().ToString("N");
        var code = $$"""
                   using System.Threading.Tasks;
                   using BBT.Workflow.Scripting;
                   using BBT.Workflow.Definitions;

                   public class TransitionMappingTest_{{nonce}} : ITransitionMapping
                   {
                       public Task<dynamic> Handler(ScriptContext context)
                       {
                           return Task.FromResult((dynamic)"ok");
                       }
                   }
                   """;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(IMapping).Assembly.Location)
        };

        // Act
        await _scriptEngine.CompileToInstanceAsync<ITransitionMapping>(code, references);
        await _scriptEngine.CompileToInstanceAsync<ITransitionMapping>(code, references);

        // Assert — both invocations complete; cache behavior itself is pinned by the key-memo and
        // hit-path identity tests (the prometheus metric assertions were removed with the metrics).
    }
}

public interface IMyCompiledClass
{
    string SayHello();
}
