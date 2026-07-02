using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
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

public sealed class StateStoreTaskExecutorTests
{
    private const string TestDomain = "test-domain";
    private const string TestWorkflow = "test-flow";
    private const string TestVersion = "1.0.0";
    private const string Store = "vnext-state";

    [Fact]
    public async Task ExecuteAsync_UnsupportedCommand_ReturnsFailureResponse()
    {
        var executor = CreateExecutor(out _);
        var context = CreateContext(CreateTask("""{ "command": "flush" }"""));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value!.ErrorMessage.ShouldContain("Unsupported state store command");
    }

    [Fact]
    public async Task ExecuteAsync_GetWithoutKey_ReturnsFailureResponse()
    {
        var executor = CreateExecutor(out _);
        var context = CreateContext(CreateTask("""{ "command": "get" }"""));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value!.ErrorMessage.ShouldContain("requires 'key'");
    }

    [Fact]
    public async Task ExecuteAsync_SetWithoutValue_ReturnsFailureResponse()
    {
        var executor = CreateExecutor(out _);
        var context = CreateContext(CreateTask("""{ "command": "set", "key": "k1" }"""));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value!.ErrorMessage.ShouldContain("requires 'value'");
    }

    [Fact]
    public async Task ExecuteAsync_GetMiss_ReturnsSuccessWithFoundFalse()
    {
        var executor = CreateExecutor(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "missing",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((default(JsonElement), string.Empty));

        var context = CreateContext(CreateTask("""{ "command": "get", "key": "missing" }"""));
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value!.Data.ShouldBeNull();
        result.Value!.Metadata!["Found"].ShouldBe(false);
    }

    [Fact]
    public async Task ExecuteAsync_GetHit_ReturnsSuccessWithDataAndETag()
    {
        var value = JsonSerializer.SerializeToElement(new { name = "Ada" });
        var executor = CreateExecutor(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "customer:42",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((value, "etag-1"));

        var context = CreateContext(CreateTask("""{ "command": "get", "key": "customer:42" }"""));
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value!.Metadata!["Found"].ShouldBe(true);
        result.Value!.Metadata!["ETag"].ShouldBe("etag-1");
    }

    [Fact]
    public async Task ExecuteAsync_Set_SavesWithTtlMetadata()
    {
        var executor = CreateExecutor(out var daprClient);
        var context = CreateContext(CreateTask("""
            { "command": "set", "key": "k1", "value": { "name": "Ada" }, "ttlInSeconds": 300 }
            """));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeTrue();
        daprClient.Verify(c => c.SaveStateAsync(
            Store, "k1", It.IsAny<JsonElement>(),
            It.IsAny<StateOptions?>(),
            It.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["ttlInSeconds"] == "300"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SetWithETag_UsesTrySaveState()
    {
        var executor = CreateExecutor(out var daprClient);
        daprClient
            .Setup(c => c.TrySaveStateAsync(
                Store, "k1", It.IsAny<JsonElement>(), "etag-1",
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var context = CreateContext(CreateTask("""
            { "command": "set", "key": "k1", "value": { "n": 1 }, "etag": "etag-1", "concurrency": "firstWrite" }
            """));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value!.Metadata!["Saved"].ShouldBe(true);
        daprClient.Verify(c => c.TrySaveStateAsync(
            Store, "k1", It.IsAny<JsonElement>(), "etag-1",
            It.Is<StateOptions>(o => o.Concurrency == ConcurrencyMode.FirstWrite),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteSingleKey_DeletesKey()
    {
        var executor = CreateExecutor(out var daprClient);
        var context = CreateContext(CreateTask("""{ "command": "delete", "key": "k1" }"""));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value!.Metadata!["DeletedCount"].ShouldBe(1);
        daprClient.Verify(c => c.DeleteStateAsync(
            Store, "k1",
            It.IsAny<StateOptions?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DeleteKeyList_DeletesBulk()
    {
        var executor = CreateExecutor(out var daprClient);
        var context = CreateContext(CreateTask("""{ "command": "delete", "keys": [ "a", "b" ] }"""));

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.Value!.IsSuccess.ShouldBeTrue();
        result.Value!.Metadata!["DeletedCount"].ShouldBe(2);
        daprClient.Verify(c => c.DeleteBulkStateAsync(
            Store,
            It.Is<IReadOnlyList<BulkDeleteStateItem>>(items => items.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DaprFailure_ReturnsFailureResponse()
    {
        var executor = CreateExecutor(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "k1",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sidecar unavailable"));

        var context = CreateContext(CreateTask("""{ "command": "get", "key": "k1" }"""));
        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsSuccess.ShouldBeFalse();
        result.Value!.ErrorMessage.ShouldContain("sidecar unavailable");
    }

    #region Helpers

    private static StateStoreTask CreateTask(string config, string key = "state-store-task")
    {
        var task = StateStoreTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(key, TestDomain, "sys-tasks", TestVersion));
        return task;
    }

    private static TaskExecutorContext CreateContext(StateStoreTask task)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var instance = Instance.Create(Guid.NewGuid(), TestWorkflow, TestVersion, "ctx-key");

        var workflow = Definitions.Workflow.Create();
        workflow.SetReference(new Reference(TestWorkflow, TestDomain, "sys-flows", TestVersion));

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .SetWorkflow(workflow)
            .Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute);
    }

    private static StateStoreTaskExecutor CreateExecutor(out Mock<DaprClient> daprClient)
    {
        daprClient = new Mock<DaprClient>();
        return new StateStoreTaskExecutor(
            daprClient.Object,
            Substitute.For<IScriptEngine>(),
            NullLogger<StateStoreTaskExecutor>.Instance);
    }

    #endregion
}
