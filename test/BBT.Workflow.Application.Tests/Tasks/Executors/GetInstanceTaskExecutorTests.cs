using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public sealed class GetInstanceTaskExecutorTests
{
    private const string LocalDomain = "test-domain";
    private const string RemoteDomain = "other-domain";

    private static TaskExecutorContext CreateContext(GetInstanceTask task)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }

    private static GetInstanceTaskExecutor CreateExecutor(
        IInstanceQueryGateway gateway,
        IRuntimeInfoProvider runtime,
        IRemoteInvokerService? remoteInvoker = null,
        IDomainDiscoveryResolver? endpointResolver = null)
        => new(
            Substitute.For<IScriptEngine>(),
            runtime,
            remoteInvoker ?? Substitute.For<IRemoteInvokerService>(),
            gateway,
            endpointResolver ?? Substitute.For<IDomainDiscoveryResolver>(),
            NullLogger<GetInstanceTaskExecutor>.Instance);

    private static GetInstanceOutput SampleOutput() => new()
    {
        Id = Guid.Parse("d2d65771-5595-44aa-b0e5-630353d87a80"),
        Key = "inst-key",
        Flow = "test-flow",
        Domain = LocalDomain,
        FlowVersion = "1.0.0",
        Metadata = new InstanceMetadataDto
        {
            CurrentState = "review",
            Status = InstanceStatus.Active,
            CreatedAt = new DateTime(2026, 07, 20, 0, 0, 0, DateTimeKind.Utc)
        },
        Attributes = JsonSerializer.SerializeToElement(new { amount = 100, currency = "TRY" })
    };

    [Fact]
    public async Task ExecuteAsync_SameDomain_ReturnsFullInstanceOutput_ViaLocalGateway()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain, flow: "test-flow", key: "inst-key");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceOutput>.Success(SampleOutput()));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        var executor = CreateExecutor(gateway, runtime, remoteInvoker);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value.StatusCode.ShouldBe(200);

        // Local path: gateway used, remote invoker not touched.
        await gateway.Received(1).GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>());
        await remoteInvoker.DidNotReceive().InvokeAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());

        var json = JsonSerializer.Serialize((object?)result.Value.Data, JsonSerializerConstants.JsonOptions);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("key").GetString().ShouldBe("inst-key");
        doc.RootElement.GetProperty("metadata").GetProperty("status").GetString().ShouldNotBeNull();
        doc.RootElement.GetProperty("attributes").GetProperty("amount").GetInt32().ShouldBe(100);
    }

    [Fact]
    public async Task ExecuteAsync_SameDomain_WhenNotModified_Returns304()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain, flow: "test-flow");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceOutput>.NotModified());

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        var executor = CreateExecutor(gateway, runtime);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.StatusCode.ShouldBe(304);
        result.Value.Metadata!["NotModified"].ShouldBe(true);
    }

    [Fact]
    public async Task ExecuteAsync_SameDomain_WhenGatewayFails_ReturnsFailureResponse()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain, flow: "test-flow");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceOutput>.Fail(
                Error.NotFound(WorkflowErrorCodes.TaskExecution, "instance not found")));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        var executor = CreateExecutor(gateway, runtime);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_CrossDomain_UsesRemoteInvoker_NotGateway()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: RemoteDomain, flow: "test-flow", key: "inst-key");

        var gateway = Substitute.For<IInstanceQueryGateway>();

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain); // current domain differs from target => remote

        var endpointResolver = Substitute.For<IDomainDiscoveryResolver>();
        endpointResolver.GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://other-domain.local"), null)));

        var remoteBody = JsonSerializer.SerializeToElement(SampleOutput(), JsonSerializerConstants.JsonOptions);
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: remoteBody, statusCode: 200, taskType: BBT.Workflow.Execution.TaskTypes.GetInstance)));

        var executor = CreateExecutor(gateway, runtime, remoteInvoker, endpointResolver);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeTrue();

        await remoteInvoker.Received(1).InvokeAsync(
            BBT.Workflow.Execution.TaskTypes.GetInstance, Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
            Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>());
        await gateway.DidNotReceive().GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Core requirement of the feature: same-domain (local gateway) and cross-domain (remote HTTP)
    /// execution must expose an identical response template to the script context, so authors can
    /// write one mapping regardless of where the target instance lives.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LocalAndRemote_ProduceIdenticalResponseShape()
    {
        var output = SampleOutput();
        var expectedJson = JsonSerializer.Serialize(output, JsonSerializerConstants.JsonOptions);

        // --- Local ---
        var localGateway = Substitute.For<IInstanceQueryGateway>();
        localGateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceOutput>.Success(output));
        var localRuntime = Substitute.For<IRuntimeInfoProvider>();
        localRuntime.Domain.Returns(LocalDomain);
        var localExecutor = CreateExecutor(localGateway, localRuntime);
        var localTask = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain, flow: "test-flow", key: "inst-key");
        var localResult = await localExecutor.ExecuteAsync(CreateContext(localTask), CancellationToken.None);

        // --- Remote --- (HTTP body is the serialized GetInstanceOutput from the same endpoint)
        var remoteRuntime = Substitute.For<IRuntimeInfoProvider>();
        remoteRuntime.Domain.Returns(LocalDomain);
        var endpointResolver = Substitute.For<IDomainDiscoveryResolver>();
        endpointResolver.GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://other-domain.local"), null)));
        var remoteBody = JsonSerializer.SerializeToElement(output, JsonSerializerConstants.JsonOptions);
        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        remoteInvoker.InvokeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: remoteBody, statusCode: 200, taskType: BBT.Workflow.Execution.TaskTypes.GetInstance)));
        var remoteExecutor = CreateExecutor(Substitute.For<IInstanceQueryGateway>(), remoteRuntime, remoteInvoker, endpointResolver);
        var remoteTask = WorkflowTaskFactory.CreateGetInstanceTask(domain: RemoteDomain, flow: "test-flow", key: "inst-key");
        var remoteResult = await remoteExecutor.ExecuteAsync(CreateContext(remoteTask), CancellationToken.None);

        localResult.IsSuccess.ShouldBeTrue();
        remoteResult.IsSuccess.ShouldBeTrue();

        var localJson = JsonSerializer.Serialize((object?)localResult.Value!.Data, JsonSerializerConstants.JsonOptions);
        var remoteJson = JsonSerializer.Serialize((object?)remoteResult.Value!.Data, JsonSerializerConstants.JsonOptions);

        localJson.ShouldBe(expectedJson);
        remoteJson.ShouldBe(expectedJson);
        localJson.ShouldBe(remoteJson);
    }
}
