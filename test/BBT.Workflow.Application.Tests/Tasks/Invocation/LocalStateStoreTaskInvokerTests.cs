using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.StateStores;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

public sealed class LocalStateStoreTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_GetOnAHit_ReturnsTheStoredValue()
    {
        var store = new FakeStateStoreClient("statestore");
        store.Seed("cfg:1", JsonSerializer.SerializeToElement(new { limit = 5 }));
        var invoker = new LocalStateStoreTaskInvoker(store, NullLogger<LocalStateStoreTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("cfg-read", GetBinding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        ((JsonElement)result.Data!).GetProperty("limit").GetInt32().ShouldBe(5);
    }

    [Fact]
    public async Task InvokeAsync_NoStoreNameConfigured_Fails()
    {
        var invoker = new LocalStateStoreTaskInvoker(
            new FakeStateStoreClient(defaultStoreName: null), NullLogger<LocalStateStoreTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("cfg-read", GetBinding("cfg:1"), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
    }

    private static JsonElement GetBinding(string key) =>
        JsonSerializer.SerializeToElement(new StateStoreBinding { Command = "get", Key = key });
}
