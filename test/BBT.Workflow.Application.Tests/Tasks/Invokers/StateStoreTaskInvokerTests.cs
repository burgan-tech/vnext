using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using Dapr.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class StateStoreTaskInvokerTests
{
    private const string Store = "vnext-state";

    [Fact]
    public async Task InvokeAsync_UnsupportedCommand_ReturnsFailure()
    {
        var invoker = CreateInvoker(out _);
        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "flushEverything",
            StoreName = Store
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Unsupported state store command");
    }

    [Fact]
    public async Task InvokeAsync_GetCacheWithoutKey_ReturnsFailure()
    {
        var invoker = CreateInvoker(out _);
        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "getCache",
            StoreName = Store
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("requires 'key'");
    }

    [Fact]
    public async Task InvokeAsync_WriteCacheWithoutValue_ReturnsFailure()
    {
        var invoker = CreateInvoker(out _);
        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "writeCache",
            StoreName = Store,
            Key = "k1"
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("requires 'value'");
    }

    [Fact]
    public async Task InvokeAsync_GetCacheMiss_ReturnsSuccessWithFoundFalse()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "missing",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((default(JsonElement), string.Empty));

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "getCache",
            StoreName = Store,
            Key = "missing"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Data.ShouldBeNull();
        result.Metadata!["Found"].ShouldBe(false);
    }

    [Fact]
    public async Task InvokeAsync_GetCacheHit_ReturnsSuccessWithData()
    {
        var value = JsonSerializer.SerializeToElement(new { name = "Ada" });
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "customer:42",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((value, "etag-1"));

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "getCache",
            StoreName = Store,
            Key = "customer:42"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["Found"].ShouldBe(true);
        result.Metadata!["ETag"].ShouldBe("etag-1");
    }

    [Fact]
    public async Task InvokeAsync_WriteCache_SavesWithTtlMetadata()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.SaveStateAsync(
                Store, "k1", It.IsAny<JsonElement>(),
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "writeCache",
            StoreName = Store,
            Key = "k1",
            Value = "{\"name\":\"Ada\"}",
            TtlInSeconds = 300
        }));

        result.IsSuccess.ShouldBeTrue();
        daprClient.Verify(c => c.SaveStateAsync(
            Store, "k1", It.IsAny<JsonElement>(),
            It.IsAny<StateOptions?>(),
            It.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["ttlInSeconds"] == "300"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_InvalidateSingleKey_DeletesKey()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.DeleteStateAsync(
                Store, "k1",
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "invalidateCache",
            StoreName = Store,
            Key = "k1"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["DeletedCount"].ShouldBe(1);
        daprClient.Verify(c => c.DeleteStateAsync(
            Store, "k1",
            It.IsAny<StateOptions?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_InvalidateKeyList_DeletesBulk()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.DeleteBulkStateAsync(
                Store,
                It.IsAny<IReadOnlyList<BulkDeleteStateItem>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "invalidateCache",
            StoreName = Store,
            Keys = ["a", "b"]
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["DeletedCount"].ShouldBe(2);
        daprClient.Verify(c => c.DeleteBulkStateAsync(
            Store,
            It.Is<IReadOnlyList<BulkDeleteStateItem>>(items => items.Count == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static StateStoreTaskInvoker CreateInvoker(out Mock<DaprClient> daprClient)
    {
        daprClient = new Mock<DaprClient>();
        return new StateStoreTaskInvoker(
            daprClient.Object,
            NullLogger<StateStoreTaskInvoker>.Instance);
    }

    private static TaskDescriptor<StateStoreBinding> Descriptor(StateStoreBinding binding) =>
        new()
        {
            TaskType = TaskTypes.StateStore,
            TaskKey = "state-store-task",
            Binding = binding
        };
}
