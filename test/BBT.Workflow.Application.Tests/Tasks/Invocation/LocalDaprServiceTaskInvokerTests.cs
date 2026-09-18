using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The in-process Dapr service-invocation invoker must produce exactly what the Execution
/// service's <c>DaprServiceTaskInvoker</c> produces for the same binding, including the
/// <see cref="TaskTypes.DaprService"/> result label — output mapping scripts and the
/// InstanceTasks journal must not be able to tell which host made the call.
/// </summary>
public sealed class LocalDaprServiceTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_SuccessfulResponse_ReturnsResultWithAppIdMetadata()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""")
        });
        var invoker = new LocalDaprServiceTaskInvoker(
            new DaprServiceInvocationClient(new HttpClient(handler)),
            NullLogger<LocalDaprServiceTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("partner-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        result.Metadata!["AppId"].ShouldBe("partner-api");
        result.TaskType.ShouldBe(TaskTypes.DaprService);
    }

    [Fact]
    public void TaskType_IsTheWireDaprServiceConstant()
    {
        var invoker = new LocalDaprServiceTaskInvoker(
            new DaprServiceInvocationClient(new HttpClient(new StubHttpMessageHandler(new HttpResponseMessage()))),
            NullLogger<LocalDaprServiceTaskInvoker>.Instance);

        invoker.TaskType.ShouldBe(TaskTypes.DaprService);
    }

    private static JsonElement Binding() => JsonSerializer.SerializeToElement(new DaprServiceBinding
    {
        AppId = "partner-api",
        MethodName = "api/v1/accounts",
        Method = "GET"
    });
}
