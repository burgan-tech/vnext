using System;
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
/// The in-process HTTP invoker must produce exactly what the Execution service's HttpTaskInvoker
/// produces for the same binding: same status/body/headers/metadata, because output mapping
/// scripts read those shapes and must not care which host made the call.
/// </summary>
public sealed class LocalHttpTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_SuccessfulJsonResponse_ReturnsParsedResult()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"orderId": 42}""")
        });
        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalHttpTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("order-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
        ((JsonElement)result.Data!).GetProperty("orderId").GetInt32().ShouldBe(42);
        result.TaskType.ShouldBe(TaskTypes.Http);
        result.Metadata!["Url"].ShouldBe("https://workflow.local/endpoint");
    }

    [Fact]
    public async Task TaskType_IsTheWireHttpConstant()
    {
        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(new StubHttpMessageHandler(new HttpResponseMessage())),
            NullLogger<LocalHttpTaskInvoker>.Instance);

        invoker.TaskType.ShouldBe(TaskTypes.Http);
    }

    [Fact]
    public async Task InvokeAsync_TransportFailure_ReturnsFailedResultNotException()
    {
        var handler = new ThrowingHttpMessageHandler(new HttpRequestException("connection refused"));
        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalHttpTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync("order-call", Binding(), traceContext: null);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();
        result.ErrorMessage.ShouldBe("connection refused");
    }

    private static JsonElement Binding() => JsonSerializer.SerializeToElement(new HttpTaskBinding
    {
        Url = "https://workflow.local/endpoint",
        Method = "GET",
        TimeoutSeconds = 30,
        ValidateSSL = true
    });
}
