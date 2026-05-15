using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Tasks;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public sealed class NotificationTaskExecutorTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string DummyScript = "// notification mapping script";

    private static NotificationTask CreateNotificationTask(string key = "notify-task")
    {
        var config = $$"""
        {
            "key": "{{key}}",
            "type": "14",
            "subject": "Test Notification",
            "to": ["user@example.com"]
        }
        """;
        var task = NotificationTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(key, TestDomain, "sys-tasks", TestVersion));
        return task;
    }

    private static TaskExecutorContext CreateContext(
        NotificationTask task,
        Dictionary<string, string?>? headers = null,
        Dictionary<string, string?>? queryParams = null)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "ctx-key");
        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));

        var builder = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .SetWorkflow(workflow)
            .SetHeaders(headers)
            .SetQueryParameters(queryParams);

        var scriptContext = builder.Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute);
    }

    private static INotificationScriptProvider CreateScriptProvider(bool hasScript = true)
    {
        var provider = Substitute.For<INotificationScriptProvider>();
        provider.GetDefaultScriptAsync(Arg.Any<CancellationToken>())
            .Returns(hasScript
                ? Result<string>.Ok(DummyScript)
                : Result<string>.Fail(Error.Failure("no-script", "No script")));
        return provider;
    }

    private static IScriptEngine CreateScriptEngine()
    {
        var mapping = Substitute.For<IMapping>();
        mapping.InputHandler(Arg.Any<WorkflowTask>(), Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse()));
        mapping.OutputHandler(Arg.Any<ScriptContext>())
            .Returns(Task.FromResult(new ScriptResponse()));

        var engine = Substitute.For<IScriptEngine>();
        engine.CompileToInstanceAsync<IMapping>(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<Microsoft.CodeAnalysis.MetadataReference>?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(mapping);
        return engine;
    }

    private static NotificationTaskExecutor CreateExecutor(
        IInstanceQueryGateway? gateway = null,
        INotificationScriptProvider? scriptProvider = null,
        ICurrentUser? currentUser = null,
        IScriptEngine? scriptEngine = null,
        IRemoteInvokerService? remoteInvoker = null)
    {
        gateway ??= Substitute.For<IInstanceQueryGateway>();
        scriptProvider ??= CreateScriptProvider();
        currentUser ??= Substitute.For<ICurrentUser>();
        scriptEngine ??= CreateScriptEngine();

        if (remoteInvoker == null)
        {
            remoteInvoker = Substitute.For<IRemoteInvokerService>();
            remoteInvoker.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
                .Returns(Result<TaskInvocationResult>.Ok(new TaskInvocationResult()));
        }

        return new NotificationTaskExecutor(
            remoteInvoker,
            scriptEngine,
            scriptProvider,
            gateway,
            currentUser,
            NullLogger<NotificationTaskExecutor>.Instance);
    }

    private static IInstanceQueryGateway CreateGateway(string state = "step-1")
    {
        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceStateOutput>.Success(new GetInstanceStateOutput
            {
                State = state,
                Status = InstanceStatus.Active,
                Data = new DataHref { Href = "/data" },
                View = new ViewHref { Href = "/view" },
                ETag = "\"etag-123\"",
                EntityEtag = "\"entity-456\"",
                Transitions = [new TransitionItem { Name = "approve", Href = "/approve" }],
                ActiveCorrelations = []
            }));
        return gateway;
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPopulateHeaders_FromScriptContext()
    {
        var task = CreateNotificationTask();
        var headers = new Dictionary<string, string?>
        {
            ["authorization"] = "Bearer token123",
            ["x-correlation-id"] = "corr-abc"
        };
        var context = CreateContext(task, headers: headers);
        var gateway = CreateGateway();

        var executor = CreateExecutor(gateway: gateway);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i =>
                i.Headers != null &&
                i.Headers["authorization"] == "Bearer token123" &&
                i.Headers["x-correlation-id"] == "corr-abc"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPopulateRole_FromCurrentUser()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = CreateGateway();

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Roles.Returns(new[] { "admin", "operator" });

        var executor = CreateExecutor(gateway: gateway, currentUser: currentUser);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i => i.Role == "admin"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleNullHeaders_Gracefully()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = CreateGateway();

        var executor = CreateExecutor(gateway: gateway);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i =>
                i.Headers != null && i.Headers.Count == 0 &&
                i.QueryParams != null && i.QueryParams.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleNullRoles_Gracefully()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = CreateGateway();

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Roles.Returns((string[]?)null);

        var executor = CreateExecutor(gateway: gateway, currentUser: currentUser);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i => i.Role == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldHandleEmptyRoles_Gracefully()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = CreateGateway();

        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Roles.Returns(Array.Empty<string>());

        var executor = CreateExecutor(gateway: gateway, currentUser: currentUser);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i => i.Role == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassQueryParams_WhenAvailable()
    {
        var task = CreateNotificationTask();
        var queryParams = new Dictionary<string, string?>
        {
            ["version"] = "2.0",
            ["extensions"] = "ext1"
        };
        var context = CreateContext(task, queryParams: queryParams);
        var gateway = CreateGateway();

        var executor = CreateExecutor(gateway: gateway);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i =>
                i.QueryParams != null &&
                i.QueryParams["version"] == "2.0" &&
                i.QueryParams["extensions"] == "ext1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnOk_WhenNoScript()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);

        var executor = CreateExecutor(scriptProvider: CreateScriptProvider(hasScript: false));
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldReturnFail_WhenStateFetchFails()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceStateOutput>.Fail(
                Error.Failure("state_fetch_failed", "Instance not found")));

        var executor = CreateExecutor(gateway: gateway);
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBindDomainAndWorkflow_FromScriptContext()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = CreateGateway();

        var executor = CreateExecutor(gateway: gateway);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i =>
                i.Domain == TestDomain &&
                i.Workflow == TestWorkflow &&
                i.Version == TestVersion),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldUseInstanceKey_WhenAvailable()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = CreateGateway();

        var executor = CreateExecutor(gateway: gateway);
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.Received(1).GetFunctionWithStateAsync(
            Arg.Is<GetFunctionWithInstanceInput>(i => i.Instance == "ctx-key"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldNotCallGateway_WhenNoScript()
    {
        var task = CreateNotificationTask();
        var context = CreateContext(task);
        var gateway = Substitute.For<IInstanceQueryGateway>();

        var executor = CreateExecutor(
            gateway: gateway,
            scriptProvider: CreateScriptProvider(hasScript: false));
        await executor.ExecuteAsync(context, CancellationToken.None);

        await gateway.DidNotReceive()
            .GetFunctionWithStateAsync(Arg.Any<GetFunctionWithInstanceInput>(), Arg.Any<CancellationToken>());
    }
}
