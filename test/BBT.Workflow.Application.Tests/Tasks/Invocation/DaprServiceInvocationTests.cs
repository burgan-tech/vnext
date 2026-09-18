using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The shared Dapr service-invocation body: one implementation, used by the Execution host's
/// invoker and by the orchestrator's in-process path. Pins the contract both depend on —
/// non-2xx comes back as a result (never an exception), reserved trace headers from the binding
/// are dropped, and the app-id/method land on the sidecar request untouched.
/// </summary>
public sealed class DaprServiceInvocationTests
{
    [Fact]
    public async Task SendAsync_ErrorStatus_ReturnsFailedResultWithBody()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("""{"error":"upstream"}""")
        });
        var client = new DaprServiceInvocationClient(new HttpClient(handler));

        var result = await DaprServiceInvocation.SendAsync(
            client, Binding(), TaskTypes.DaprService, CancellationToken.None, taskKey: "probe");

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(502);
        result.Body.ShouldBe("""{"error":"upstream"}""");
        result.Metadata!["AppId"].ShouldBe("partner-api");
    }

    [Fact]
    public async Task SendAsync_ReservedTraceHeaderInBinding_IsNotCopiedOntoTheRequest()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DaprServiceInvocationClient(new HttpClient(handler));
        var binding = Binding(headers: """{"traceparent":"00-deadbeef-0000-01","x-custom":"keep"}""");

        await DaprServiceInvocation.SendAsync(
            client, binding, TaskTypes.DaprService, CancellationToken.None, taskKey: "probe");

        handler.LastRequest!.Headers.Contains("traceparent").ShouldBeFalse();
        handler.LastRequest.Headers.Contains("x-custom").ShouldBeTrue();
    }

    private static DaprServiceBinding Binding(string? headers = null) => new()
    {
        AppId = "partner-api",
        MethodName = "api/v1/accounts",
        Method = "POST",
        Body = """{"id":1}""",
        Headers = headers
    };
}
