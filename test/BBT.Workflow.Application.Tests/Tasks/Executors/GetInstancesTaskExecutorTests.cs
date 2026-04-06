using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public sealed class GetInstancesTaskExecutorTests
{
    private static TaskExecutorContext CreateContext(GetInstancesTask task)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute);
    }

    [Fact]
    public async Task ExecuteAsync_WhenListReturnsGroups_SkipsGetInstanceData_AndReturnsGroupedMetadata()
    {
        var task = WorkflowTaskFactory.CreateGetInstancesTask(
            domain: "test-domain",
            flow: "test-flow",
            filter: """{"groupBy":["status"]}""");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        var groups = new List<GroupSummary>
        {
            new() { Name = "open", Count = 2 },
            new() { Name = "done", Count = 3 }
        };
        gateway.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(InstanceListWithGroupsResponse<GetInstanceOutput>.FromGroups(groups)));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var urlBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlBuilder.BuildFunctionListUrl("test-domain", "test-flow", "data")
            .Returns("/api/test-domain/workflows/test-flow/functions/data");

        var executor = new GetInstancesTaskExecutor(
            Substitute.For<IScriptEngine>(),
            runtime,
            Substitute.For<IRemoteInvokerService>(),
            gateway,
            Substitute.For<IDomainDiscoveryResolver>(),
            urlBuilder,
            NullLogger<GetInstancesTaskExecutor>.Instance);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await gateway.DidNotReceive()
            .GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>());

        var response = result.Value!;
        response.IsSuccess.ShouldBeTrue();
        response.Metadata!["Grouped"].ShouldBe(true);
        response.Metadata["ItemCount"].ShouldBe(2);

        var dataJson = JsonSerializer.Serialize(response.Data, JsonSerializerConstants.JsonOptions);
        using var doc = JsonDocument.Parse(dataJson);
        Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("open", doc.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_WhenListReturnsInstances_CallsGetInstanceDataPerItem()
    {
        var task = WorkflowTaskFactory.CreateGetInstancesTask(domain: "test-domain", flow: "test-flow");

        var instanceRow = new GetInstanceOutput { Key = "inst-1" };
        var listResponse = new InstanceListWithGroupsResponse<GetInstanceOutput>();
        listResponse.Items.Add(instanceRow);

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(listResponse));
        gateway.GetInstanceDataAsync(
                Arg.Is<GetInstanceDataInput>(i => i.Instance == "inst-1"),
                Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.Success(new GetInstanceDataOutput()));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var urlBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlBuilder.BuildFunctionListUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("/fn/data");

        var executor = new GetInstancesTaskExecutor(
            Substitute.For<IScriptEngine>(),
            runtime,
            Substitute.For<IRemoteInvokerService>(),
            gateway,
            Substitute.For<IDomainDiscoveryResolver>(),
            urlBuilder,
            NullLogger<GetInstancesTaskExecutor>.Instance);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await gateway.Received(1)
            .GetInstanceDataAsync(
                Arg.Is<GetInstanceDataInput>(i => i.Instance == "inst-1"),
                Arg.Any<CancellationToken>());
        result.Value!.Metadata!.ContainsKey("Grouped").ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenItemsAreJsonElements_DeserializesAsGroupSummaries()
    {
        var task = WorkflowTaskFactory.CreateGetInstancesTask(domain: "test-domain", flow: "test-flow");

        var listResponse = new InstanceListWithGroupsResponse<GetInstanceOutput>
        {
            Items =
            [
                JsonSerializer.SerializeToElement(
                    new GroupSummary { Name = "a", Count = 5 },
                    JsonSerializerConstants.JsonOptions)
            ]
        };

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(listResponse));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var urlBuilder = Substitute.For<IUrlTemplateBuilder>();
        urlBuilder.BuildFunctionListUrl(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns("/fn/data");

        var executor = new GetInstancesTaskExecutor(
            Substitute.For<IScriptEngine>(),
            runtime,
            Substitute.For<IRemoteInvokerService>(),
            gateway,
            Substitute.For<IDomainDiscoveryResolver>(),
            urlBuilder,
            NullLogger<GetInstancesTaskExecutor>.Instance);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await gateway.DidNotReceive()
            .GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>());
        result.Value!.Metadata!["Grouped"].ShouldBe(true);
    }
}
