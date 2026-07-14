using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.StateStores;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invokers;

public sealed class CacheAsideTaskInvokerTests
{
    private const string Store = "vnext-state";

    [Fact]
    public async Task InvokeAsync_CacheHit_ReturnsCachedValue_WithoutRunningSource()
    {
        var value = JsonSerializer.SerializeToElement(new { name = "Ada" });
        var invoker = CreateInvoker(out var daprClient, out var registry);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "custom:customer:42", It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((value, "etag-1"));

        var result = await invoker.InvokeAsync(Descriptor(Binding("customer:42")));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["CacheHit"].ShouldBe(true);
        // Source task not invoked on a hit.
        registry.Verify(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_CacheMiss_RunsSource_AndWritesBackWithTtl()
    {
        var invoker = CreateInvoker(out var daprClient, out var registry);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "custom:k1", It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((default(JsonElement), string.Empty));
        var sourceValue = JsonSerializer.SerializeToElement(new { id = 7 });
        registry
            .Setup(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskInvocationResult.Success(data: sourceValue));

        var result = await invoker.InvokeAsync(Descriptor(Binding("k1", ttlInSeconds: 300)));

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["CacheHit"].ShouldBe(false);
        registry.Verify(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        daprClient.Verify(c => c.SaveStateAsync(
            Store, "custom:k1", It.IsAny<object>(), It.IsAny<StateOptions?>(),
            It.Is<IReadOnlyDictionary<string, string>>(m => m != null && m["ttlInSeconds"] == "300"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ForceRefresh_SkipsRead_RunsSource_AndWritesBack()
    {
        var invoker = CreateInvoker(out var daprClient, out var registry);
        registry
            .Setup(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskInvocationResult.Success(data: JsonSerializer.SerializeToElement(new { id = 9 })));

        var result = await invoker.InvokeAsync(Descriptor(Binding("k1", forceRefresh: true)));

        result.IsSuccess.ShouldBeTrue();
        daprClient.Verify(c => c.GetStateAndETagAsync<JsonElement>(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsistencyMode?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Never);
        daprClient.Verify(c => c.SaveStateAsync(
            Store, "custom:k1", It.IsAny<object>(), It.IsAny<StateOptions?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task InvokeAsync_ReadError_WithoutBypass_Fails()
    {
        var invoker = CreateInvoker(out var daprClient, out var registry);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "custom:k1", It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var result = await invoker.InvokeAsync(Descriptor(Binding("k1", bypassOnCacheError: false)));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("CacheAside read failed");
        registry.Verify(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InvokeAsync_ReadError_WithBypass_FallsBackToSource()
    {
        var invoker = CreateInvoker(out var daprClient, out var registry);
        daprClient
            .Setup(c => c.GetStateAndETagAsync<JsonElement>(
                Store, "custom:k1", It.IsAny<ConsistencyMode?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        registry
            .Setup(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TaskInvocationResult.Success(data: JsonSerializer.SerializeToElement(new { id = 1 })));

        var result = await invoker.InvokeAsync(Descriptor(Binding("k1", bypassOnCacheError: true)));

        result.IsSuccess.ShouldBeTrue();
        registry.Verify(r => r.InvokeAsync(It.IsAny<TaskEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static CacheAsideBinding Binding(
        string key,
        int? ttlInSeconds = null,
        bool forceRefresh = false,
        bool bypassOnCacheError = true) => new()
    {
        Key = key,
        StoreName = Store,
        TtlInSeconds = ttlInSeconds,
        Consistency = "Eventual",
        BypassOnCacheError = bypassOnCacheError,
        ForceRefresh = forceRefresh,
        SourceTask = new TaskEnvelope
        {
            TaskType = "http",
            TaskKey = "get-customer-http",
            Binding = JsonSerializer.SerializeToElement(new { url = "https://example/get" })
        }
    };

    private static TaskDescriptor<CacheAsideBinding> Descriptor(CacheAsideBinding binding) =>
        new()
        {
            TaskType = TaskTypes.CacheAside,
            TaskKey = "cache-aside-task",
            Binding = binding
        };

    private static CacheAsideTaskInvoker CreateInvoker(
        out Mock<DaprClient> daprClient,
        out Mock<ITaskInvokerRegistry> registry)
    {
        daprClient = new Mock<DaprClient>();
        registry = new Mock<ITaskInvokerRegistry>();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DAPR_STATE_STORE_NAME"] = Store })
            .Build();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(s => s.GetService(typeof(ITaskInvokerRegistry))).Returns(registry.Object);

        return new CacheAsideTaskInvoker(
            new DaprStateStoreClient(daprClient.Object, configuration),
            serviceProvider.Object,
            NullLogger<CacheAsideTaskInvoker>.Instance);
    }
}
