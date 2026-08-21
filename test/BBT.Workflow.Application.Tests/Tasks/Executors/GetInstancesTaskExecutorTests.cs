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

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }

    private static GetInstancesTaskExecutor CreateExecutor(
        IInstanceQueryGateway gateway,
        IRuntimeInfoProvider runtime)
        => new(
            Substitute.For<IScriptEngine>(),
            runtime,
            Substitute.For<IRemoteInvokerService>(),
            gateway,
            Substitute.For<IDomainDiscoveryResolver>(),
            NullLogger<GetInstancesTaskExecutor>.Instance);

    [Fact]
    public async Task ExecuteAsync_WhenListReturnsGroups_ReturnsGroupedMetadata_AndPassesThroughResponse()
    {
        // groupBy takes an object, not a bare array. The array form used to be silently dropped by
        // the parser, so the task ran unfiltered and ungrouped while this test still passed off a
        // stubbed gateway; it is now rejected up front.
        var task = WorkflowTaskFactory.CreateGetInstancesTask(
            domain: "test-domain",
            flow: "test-flow",
            filter: """{"groupBy":{"fields":["status"]}}""");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        var groups = new List<GroupSummary>
        {
            new()
            {
                Name = "open",
                Count = 2,
                Keys = new Dictionary<string, object?> { ["status"] = "open" }
            },
            new()
            {
                Name = "done",
                Count = 3,
                Keys = new Dictionary<string, object?> { ["status"] = "done" }
            }
        };
        gateway.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(InstanceListWithGroupsResponse<GetInstanceOutput>.FromGroups(groups)));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var executor = CreateExecutor(gateway, runtime);

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
        var keys0 = doc.RootElement.GetProperty("items")[0].GetProperty("keys");
        Assert.Equal("open", keys0.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_WhenListReturnsMultiKeyGroups_SerializesKeysPerField()
    {
        var task = WorkflowTaskFactory.CreateGetInstancesTask(
            domain: "test-domain",
            flow: "test-flow",
            filter: """{"groupBy":{"fields":["attributes.scope","attributes.channel"]}}""");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        var groups = new List<GroupSummary>
        {
            new()
            {
                Name = "scope-a_EFT",
                Sum = 1000m,
                Keys = new Dictionary<string, object?>
                {
                    ["attributes.scope"] = "scope-a",
                    ["attributes.channel"] = "EFT"
                }
            }
        };
        gateway.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(InstanceListWithGroupsResponse<GetInstanceOutput>.FromGroups(groups)));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var executor = CreateExecutor(gateway, runtime);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var dataJson = JsonSerializer.Serialize(result.Value!.Data, JsonSerializerConstants.JsonOptions);
        using var doc = JsonDocument.Parse(dataJson);
        var item0 = doc.RootElement.GetProperty("items")[0];
        Assert.Equal("scope-a_EFT", item0.GetProperty("name").GetString());
        Assert.Equal(1000m, item0.GetProperty("sum").GetDecimal());
        var keys = item0.GetProperty("keys");
        Assert.Equal("scope-a", keys.GetProperty("attributes.scope").GetString());
        Assert.Equal("EFT", keys.GetProperty("attributes.channel").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_WhenListReturnsInstances_ReturnsResponseAsIs_WithoutGroupedFlag()
    {
        var task = WorkflowTaskFactory.CreateGetInstancesTask(domain: "test-domain", flow: "test-flow");

        var instanceRow = new GetInstanceOutput { Key = "inst-1" };
        var listResponse = new InstanceListWithGroupsResponse<GetInstanceOutput>();
        listResponse.Items.Add(instanceRow);

        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(listResponse));

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var executor = CreateExecutor(gateway, runtime);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // The executor no longer fans out to GetInstanceDataAsync; it returns the list response as-is.
        await gateway.DidNotReceive()
            .GetInstanceDataAsync(Arg.Any<GetInstanceDataInput>(), Arg.Any<CancellationToken>());
        result.Value!.Metadata!.ContainsKey("Grouped").ShouldBeFalse();
        result.Value!.Metadata!["ItemCount"].ShouldBe(1);
    }

    [Theory]
    // Unsupported operator using the schema-side spelling.
    [InlineData("""{"attributes":{"amount":{"gte":100}}}""")]
    // Entirely unknown operator.
    [InlineData("""{"attributes":{"amount":{"zzz":100}}}""")]
    // Truncated so it no longer ends with a brace.
    [InlineData("""{"attributes":{"amount":{"eq":100""")]
    public async Task ExecuteAsync_ShouldFail_WhenTaskFilterCannotBeExecuted(string filter)
    {
        // A task filter is authored in a versioned workflow definition, so an unexecutable one is a
        // definition defect. It must fail rather than degrade into an unfiltered read that loads
        // every instance of the target workflow into instance data.
        var task = WorkflowTaskFactory.CreateGetInstancesTask(
            domain: "test-domain", flow: "test-flow", filter: filter);

        var gateway = Substitute.For<IInstanceQueryGateway>();
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var result = await CreateExecutor(gateway, runtime)
            .ExecuteAsync(CreateContext(task), CancellationToken.None);

        // InvokeAsync returns Result.Fail, which the base routes through CreateErrorResponse. That
        // path bypasses the task's acceptedStatusCodes, so a definition defect cannot be
        // whitelisted into looking successful.
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value.ErrorMessage.ShouldContain("invalid query");

        await gateway.DidNotReceive()
            .GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFail_WhenTaskSortCannotBeExecuted()
    {
        var task = WorkflowTaskFactory.CreateGetInstancesTask(
            domain: "test-domain", flow: "test-flow", sort: """{"field":"nope"}""");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var result = await CreateExecutor(gateway, runtime)
            .ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeFalse();

        await gateway.DidNotReceive()
            .GetInstanceListAsync(Arg.Any<GetInstanceListInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldValidateBeforeChoosingTheRemotePath()
    {
        // Cross-domain: the bad filter must never leave the box.
        var task = WorkflowTaskFactory.CreateGetInstancesTask(
            domain: "other-domain", flow: "test-flow",
            filter: """{"attributes":{"amount":{"gte":100}}}""");

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns("test-domain");

        var remoteInvoker = Substitute.For<IRemoteInvokerService>();
        var executor = new GetInstancesTaskExecutor(
            Substitute.For<IScriptEngine>(),
            runtime,
            remoteInvoker,
            Substitute.For<IInstanceQueryGateway>(),
            Substitute.For<IDomainDiscoveryResolver>(),
            NullLogger<GetInstancesTaskExecutor>.Instance);

        var result = await executor.ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeFalse();
        await remoteInvoker.DidNotReceiveWithAnyArgs()
            .InvokeAsync(default!, default!, default!, default!, default);
    }
}
