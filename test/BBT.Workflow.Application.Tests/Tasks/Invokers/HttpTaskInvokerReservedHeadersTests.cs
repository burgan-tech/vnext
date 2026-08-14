using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

/// <summary>
/// Pins the reserved-header guard: task binding header definitions must not be able to overwrite
/// live trace/correlation headers on outbound calls — a stale traceparent copied into a binding
/// would detach the downstream service from the current trace.
/// </summary>
public sealed class HttpTaskInvokerReservedHeadersTests
{
    [Theory]
    [InlineData("traceparent")]
    [InlineData("TraceParent")]
    [InlineData("tracestate")]
    [InlineData("baggage")]
    [InlineData("x-request-id")]
    [InlineData("X-Request-Id")]
    public async Task InvokeAsync_ReservedTraceHeaderInBinding_IsNotCopiedToRequest(string headerName)
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);

        var descriptor = CreateDescriptor(
            headers: $$"""{"{{headerName}}":"stale-value","X-Custom":"kept"}""");

        await invoker.InvokeAsync(descriptor);

        handler.RequestHeaderContains(headerName).ShouldBeFalse();
        handler.RequestHeaderContains("X-Custom").ShouldBeTrue();
    }

    [Fact]
    public void IsReservedTraceHeader_MatchesCaseInsensitive()
    {
        InvokerHelpers.IsReservedTraceHeader("TRACEPARENT").ShouldBeTrue();
        InvokerHelpers.IsReservedTraceHeader("TraceState").ShouldBeTrue();
        InvokerHelpers.IsReservedTraceHeader("Baggage").ShouldBeTrue();
        InvokerHelpers.IsReservedTraceHeader("X-REQUEST-ID").ShouldBeTrue();
        InvokerHelpers.IsReservedTraceHeader("Authorization").ShouldBeFalse();
        InvokerHelpers.IsReservedTraceHeader("X-Custom").ShouldBeFalse();
    }

    private static HttpTaskInvoker CreateInvoker(CapturingHttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<HttpTaskInvoker>.Instance);

    private static TaskDescriptor<HttpTaskBinding> CreateDescriptor(string headers) =>
        new()
        {
            TaskType = TaskTypes.Http,
            TaskKey = "http-task",
            Binding = new HttpTaskBinding
            {
                Url = "https://workflow.local/endpoint",
                Method = "POST",
                Body = "{}",
                Headers = headers
            }
        };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private HttpRequestHeaders? _requestHeaders;

        public bool RequestHeaderContains(string name) =>
            _requestHeaders?.NonValidated.Contains(name) ?? false;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestHeaders = request.Headers;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
