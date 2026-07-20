using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.StateStores;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
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
    public async Task InvokeAsync_GetWithoutKey_ReturnsFailure()
    {
        var invoker = CreateInvoker(out _);
        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "get",
            StoreName = Store
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("requires 'key'");
    }

    [Fact]
    public async Task InvokeAsync_SetWithoutValue_ReturnsFailure()
    {
        var invoker = CreateInvoker(out _);
        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "set",
            StoreName = Store,
            Key = "k1"
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("requires 'value'");
    }

    [Fact]
    public async Task InvokeAsync_GetMiss_ReturnsSuccessWithFoundFalse()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "custom:missing",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((default(JsonElement), string.Empty));

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "get",
            StoreName = Store,
            Key = "missing"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Data.ShouldBeNull();
        result.Metadata!["Found"].ShouldBe(false);
    }

    [Fact]
    public async Task InvokeAsync_GetHit_ReturnsSuccessWithData()
    {
        var value = JsonSerializer.SerializeToElement(new { name = "Ada" });
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "custom:customer:42",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((value, "etag-1"));

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "get",
            StoreName = Store,
            Key = "customer:42"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["Found"].ShouldBe(true);
        result.Metadata!["ETag"].ShouldBe("etag-1");
    }

    [Fact]
    public async Task InvokeAsync_Set_SavesWithTtlMetadata()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.SaveStateAsync(
                Store, "custom:k1", It.IsAny<JsonElement>(),
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "set",
            StoreName = Store,
            Key = "k1",
            Value = "{\"name\":\"Ada\"}",
            TtlInSeconds = 300
        }));

        result.IsSuccess.ShouldBeTrue();
        daprClient.Verify(c => c.SaveStateAsync(
            Store, "custom:k1", It.IsAny<JsonElement>(),
            It.IsAny<StateOptions?>(),
            It.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["ttlInSeconds"] == "300"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_DeleteSingleKey_DeletesKey()
    {
        var invoker = CreateInvoker(out var daprClient);
        daprClient
            .Setup(c => c.DeleteStateAsync(
                Store, "custom:k1",
                It.IsAny<StateOptions?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "delete",
            StoreName = Store,
            Key = "k1"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["DeletedCount"].ShouldBe(1);
        result.Metadata!["Key"].ShouldBe("custom:k1");
        daprClient.Verify(c => c.DeleteStateAsync(
            Store, "custom:k1",
            It.IsAny<StateOptions?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_DeleteKeyList_DeletesBulk()
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
            Command = "delete",
            StoreName = Store,
            Keys = ["a", "b"]
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["DeletedCount"].ShouldBe(2);
        daprClient.Verify(c => c.DeleteBulkStateAsync(
            Store,
            It.Is<IReadOnlyList<BulkDeleteStateItem>>(items =>
                items.Count == 2 && items.All(i => i.Key.StartsWith("custom:"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithoutStoreName_UsesConfiguredDefault()
    {
        var invoker = CreateInvoker(out var daprClient, configuredStoreName: "env-store");
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                "env-store", "custom:k1",
                It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((default(JsonElement), string.Empty));

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "get",
            Key = "k1"
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["StoreName"].ShouldBe("env-store");
        daprClient.Verify(c => c.GetStateAndETagAsync<JsonElement>(
            "env-store", "custom:k1",
            It.IsAny<ConsistencyMode?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_WithoutStoreNameOrConfiguration_ReturnsFailure()
    {
        var invoker = CreateInvoker(out _, configuredStoreName: null);

        var result = await invoker.InvokeAsync(Descriptor(new StateStoreBinding
        {
            Command = "get",
            Key = "k1"
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("State store name is not configured");
        result.ErrorMessage.ShouldContain("DAPR_STATE_STORE_NAME");
    }

    private static StateStoreTaskInvoker CreateInvoker(
        out Mock<DaprClient> daprClient,
        string? configuredStoreName = Store)
    {
        daprClient = new Mock<DaprClient>();

        var values = new Dictionary<string, string?>();
        if (configuredStoreName is not null)
        {
            values["DAPR_STATE_STORE_NAME"] = configuredStoreName;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        return new StateStoreTaskInvoker(
            new DaprStateStoreClient(daprClient.Object, configuration),
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
