using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Notification;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Dapr.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public sealed class NotificationTaskExecutorTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";

    #region Helpers

    private static NotificationTask CreateTask(
        string key = "notify-task",
        string[]? channels = null,
        bool includeStateChannel = false)
    {
        var channelList = channels ?? ["sms", "email"];
        var channelsJson = JsonSerializer.Serialize(channelList);
        var config = $$"""
        {
            "key": "{{key}}",
            "type": "14",
            "channels": {{channelsJson}},
            "includeStateChannel": {{includeStateChannel.ToString().ToLowerInvariant()}}
        }
        """;
        var task = NotificationTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(key, TestDomain, "sys-tasks", TestVersion));
        return task;
    }

    private static TaskExecutorContext CreateContext(
        NotificationTask task,
        string? mappingCode = null)
    {
        var scriptCode = !string.IsNullOrEmpty(mappingCode)
            ? ScriptCode.FromNative(mappingCode)
            : ScriptCode.FromNative(string.Empty);

        var onExecute = OnExecuteTask.Create(1, task, scriptCode);
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "ctx-key");

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .SetWorkflow(workflow)
            .Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }

    private static NotificationTaskExecutor CreateExecutor(
        Mock<DaprClient>? daprClient = null,
        INotificationChannelResolver? channelResolver = null,
        IStateChannelMessageBuilder? stateBuilder = null,
        IScriptEngine? scriptEngine = null)
    {
        daprClient ??= new Mock<DaprClient>();
        channelResolver ??= new TestChannelResolver();
        stateBuilder ??= CreateDefaultStateBuilder();
        scriptEngine ??= Substitute.For<IScriptEngine>();

        return new NotificationTaskExecutor(
            daprClient.Object,
            channelResolver,
            stateBuilder,
            scriptEngine,
            NullLogger<NotificationTaskExecutor>.Instance,
            Substitute.For<IWorkflowMetrics>());
    }

    private static IStateChannelMessageBuilder CreateDefaultStateBuilder()
    {
        var builder = Substitute.For<IStateChannelMessageBuilder>();
        builder.BuildAsync(
                Arg.Any<ScriptContext>(),
                Arg.Any<IStateNotificationMapping?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<NotificationMessage>.Ok(new NotificationMessage
            {
                Data = new { state = "step-1", status = "Active" }
            }));
        return builder;
    }

    private static INotificationMapping CreateMapping(
        Func<string, ScriptContext, Task<NotificationMessage?>>? handler = null)
    {
        var mapping = Substitute.For<INotificationMapping>();
        mapping.ChannelHandler(Arg.Any<string>(), Arg.Any<ScriptContext>())
            .Returns(ci =>
            {
                if (handler is not null)
                    return handler(ci.Arg<string>(), ci.Arg<ScriptContext>());

                return Task.FromResult<NotificationMessage?>(new NotificationMessage
                {
                    Data = new { channel = ci.Arg<string>() }
                });
            });
        return mapping;
    }

    private static IScriptEngine CreateScriptEngineWithMapping(
        INotificationMapping mapping,
        IStateNotificationMapping? stateMapping = null)
    {
        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToInstanceAsync<INotificationMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
                Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(mapping);

        if (stateMapping is not null)
        {
            engine.CompileToInstanceAsync<IStateNotificationMapping>(
                    Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
                    Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                    Arg.Any<IEnumerable<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns(stateMapping);
        }
        else
        {
            engine.CompileToInstanceAsync<IStateNotificationMapping>(
                    Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
                    Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                    Arg.Any<IEnumerable<string>?>(),
                    Arg.Any<CancellationToken>())
                .Returns<IStateNotificationMapping>(_ =>
                    throw new InvalidOperationException("No type implementing IStateNotificationMapping found."));
        }

        return engine;
    }

    private sealed class TestChannelResolver : INotificationChannelResolver
    {
        public string ResolveBindingName(string channel) =>
            $"vnext-notification-{channel.ToLowerInvariant()}";
    }

    #endregion

    [Fact]
    public async Task ExecuteAsync_MultiChannel_DispatchesAllChannels()
    {
        var daprClient = new Mock<DaprClient>();
        var mapping = CreateMapping();
        var scriptEngine = CreateScriptEngineWithMapping(mapping);

        var task = CreateTask(channels: ["sms", "email"]);
        var context = CreateContext(task, mappingCode: "// mapping");

        var executor = CreateExecutor(daprClient: daprClient, scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-sms"),
            It.IsAny<CancellationToken>()), Times.Once);

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-email"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_StateChannel_UsesStateChannelBuilder()
    {
        var daprClient = new Mock<DaprClient>();
        var stateBuilder = Substitute.For<IStateChannelMessageBuilder>();
        stateBuilder.BuildAsync(
                Arg.Any<ScriptContext>(),
                Arg.Any<IStateNotificationMapping?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<NotificationMessage>.Ok(new NotificationMessage
            {
                Data = new { state = "active-state" }
            }));

        var task = CreateTask(channels: [], includeStateChannel: true);
        var context = CreateContext(task);

        var executor = CreateExecutor(daprClient: daprClient, stateBuilder: stateBuilder);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await stateBuilder.Received(1).BuildAsync(
            Arg.Any<ScriptContext>(),
            Arg.Any<IStateNotificationMapping?>(),
            Arg.Any<CancellationToken>());

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-state"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NullMappingReturn_SkipsChannel()
    {
        var daprClient = new Mock<DaprClient>();
        var mapping = CreateMapping(handler: (channel, _) =>
            channel == "sms"
                ? Task.FromResult<NotificationMessage?>(null)
                : Task.FromResult<NotificationMessage?>(new NotificationMessage { Data = "ok" }));
        var scriptEngine = CreateScriptEngineWithMapping(mapping);

        var task = CreateTask(channels: ["sms", "email"]);
        var context = CreateContext(task, mappingCode: "// mapping");

        var executor = CreateExecutor(daprClient: daprClient, scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-sms"),
            It.IsAny<CancellationToken>()), Times.Never);

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-email"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoChannels_ReturnsSuccessWithNoDispatch()
    {
        var daprClient = new Mock<DaprClient>();
        var task = CreateTask(channels: [], includeStateChannel: false);
        var context = CreateContext(task);

        var executor = CreateExecutor(daprClient: daprClient);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        daprClient.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ChannelError_IsolatedPerChannel()
    {
        var daprClient = new Mock<DaprClient>();
        daprClient
            .Setup(c => c.InvokeBindingAsync(
                It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-sms"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Dapr sidecar not available"));

        var mapping = CreateMapping();
        var scriptEngine = CreateScriptEngineWithMapping(mapping);

        var task = CreateTask(channels: ["sms", "email"]);
        var context = CreateContext(task, mappingCode: "// mapping");

        var executor = CreateExecutor(daprClient: daprClient, scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-email"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_IncludeStateChannel_DoesNotDuplicate()
    {
        var daprClient = new Mock<DaprClient>();
        var mapping = CreateMapping();
        var scriptEngine = CreateScriptEngineWithMapping(mapping);

        var task = CreateTask(channels: ["state", "email"], includeStateChannel: true);
        var context = CreateContext(task, mappingCode: "// mapping");

        var executor = CreateExecutor(daprClient: daprClient, scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        daprClient.Verify(c => c.InvokeBindingAsync(
            It.Is<BindingRequest>(r => r.BindingName == "vnext-notification-state"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NoMapping_NonStateChannel_FailsGracefully()
    {
        var daprClient = new Mock<DaprClient>();
        var task = CreateTask(channels: ["sms"]);
        var context = CreateContext(task);

        var executor = CreateExecutor(daprClient: daprClient);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var invocationResult = result.Value!;
        var errors = (List<string>)invocationResult.Metadata!["errors"];
        errors.Count.ShouldBe(1);
        errors[0].ShouldContain("requires an INotificationMapping script");
    }

    [Fact]
    public async Task ExecuteAsync_MetadataIncludesDispatchSummary()
    {
        var mapping = CreateMapping(handler: (channel, _) =>
            channel == "sms"
                ? Task.FromResult<NotificationMessage?>(null)
                : Task.FromResult<NotificationMessage?>(new NotificationMessage { Data = "ok" }));
        var scriptEngine = CreateScriptEngineWithMapping(mapping);

        var task = CreateTask(channels: ["sms", "email"]);
        var context = CreateContext(task, mappingCode: "// mapping");

        var executor = CreateExecutor(scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var invocationResult = result.Value!;
        var dispatched = (List<string>)invocationResult.Metadata!["dispatched"];
        var skipped = (List<string>)invocationResult.Metadata!["skipped"];
        dispatched.ShouldContain("email");
        skipped.ShouldContain("sms");
    }

    [Fact]
    public async Task ExecuteAsync_StateChannel_PassesStateMappingToBuilder()
    {
        var daprClient = new Mock<DaprClient>();

        var stateMapping = Substitute.For<IStateNotificationMapping>();
        stateMapping.EnrichAsync(Arg.Any<ScriptContext>())
            .Returns(new StateNotificationMetadata
            {
                Metadata = new Dictionary<string, string>
                {
                    ["X-Device-Id"] = "device-123",
                    ["X-Token-Id"] = "token-456"
                }
            });

        var mapping = CreateMapping();
        var scriptEngine = CreateScriptEngineWithMapping(mapping, stateMapping);

        var stateBuilder = Substitute.For<IStateChannelMessageBuilder>();
        stateBuilder.BuildAsync(
                Arg.Any<ScriptContext>(),
                Arg.Any<IStateNotificationMapping?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<NotificationMessage>.Ok(new NotificationMessage
            {
                Data = new { state = "step-1" },
                Metadata = new Dictionary<string, string>
                {
                    ["instanceId"] = "inst-1",
                    ["X-Device-Id"] = "device-123"
                }
            }));

        var task = CreateTask(channels: ["email"], includeStateChannel: true);
        var context = CreateContext(task, mappingCode: "// mapping with state");

        var executor = CreateExecutor(
            daprClient: daprClient, stateBuilder: stateBuilder, scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await stateBuilder.Received(1).BuildAsync(
            Arg.Any<ScriptContext>(),
            stateMapping,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StateChannel_NoStateMappingInScript_PassesNull()
    {
        var daprClient = new Mock<DaprClient>();
        var mapping = CreateMapping();
        var scriptEngine = CreateScriptEngineWithMapping(mapping, stateMapping: null);

        var stateBuilder = Substitute.For<IStateChannelMessageBuilder>();
        stateBuilder.BuildAsync(
                Arg.Any<ScriptContext>(),
                Arg.Any<IStateNotificationMapping?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<NotificationMessage>.Ok(new NotificationMessage
            {
                Data = new { state = "step-1" }
            }));

        var task = CreateTask(channels: [], includeStateChannel: true);
        var context = CreateContext(task, mappingCode: "// mapping without state");

        var executor = CreateExecutor(
            daprClient: daprClient, stateBuilder: stateBuilder, scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await stateBuilder.Received(1).BuildAsync(
            Arg.Any<ScriptContext>(),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_StateOnly_DoesNotCompileINotificationMapping()
    {
        var scriptEngine = Substitute.For<IScriptEngine>();
        scriptEngine.CompileToInstanceAsync<IStateNotificationMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
                Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns<IStateNotificationMapping>(_ =>
                throw new InvalidOperationException("Not found"));

        var task = CreateTask(channels: [], includeStateChannel: true);
        var context = CreateContext(task, mappingCode: "// state only script");

        var executor = CreateExecutor(scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await scriptEngine.DidNotReceive().CompileToInstanceAsync<INotificationMapping>(
            Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
            Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NonStateOnly_DoesNotCompileIStateNotificationMapping()
    {
        var mapping = CreateMapping();
        var scriptEngine = Substitute.For<IScriptEngine>();
        scriptEngine.CompileToInstanceAsync<INotificationMapping>(
                Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
                Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(mapping);

        var task = CreateTask(channels: ["sms", "email"], includeStateChannel: false);
        var context = CreateContext(task, mappingCode: "// non-state script");

        var executor = CreateExecutor(scriptEngine: scriptEngine);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await scriptEngine.DidNotReceive().CompileToInstanceAsync<IStateNotificationMapping>(
            Arg.Any<ScriptCode>(), Arg.Any<ScriptSettings>(),
            Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>());
    }
}
