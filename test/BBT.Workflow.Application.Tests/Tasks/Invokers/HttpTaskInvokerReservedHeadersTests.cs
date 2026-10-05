using System.Linq;
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
    [InlineData("X-Correlation-Id")]
    [InlineData("X-Workflow-Instance-Id")]
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
        InvokerHelpers.IsReservedTraceHeader("X-CORRELATION-ID").ShouldBeTrue();
        InvokerHelpers.IsReservedTraceHeader("X-Workflow-Instance-Id").ShouldBeTrue();
        // Identity claims are NOT reserved: a developer may set them in the binding (fill-if-absent).
        InvokerHelpers.IsReservedTraceHeader("SUB").ShouldBeFalse();
        InvokerHelpers.IsReservedTraceHeader("Act_Sub").ShouldBeFalse();
        // X-Request-Id is NOT reserved either: APIs such as OHVPS/BKM require the mapping's value.
        InvokerHelpers.IsReservedTraceHeader("X-REQUEST-ID").ShouldBeFalse();
        InvokerHelpers.IsReservedTraceHeader("Authorization").ShouldBeFalse();
        InvokerHelpers.IsReservedTraceHeader("X-Custom").ShouldBeFalse();
    }

    /// <summary>
    /// The Execution host's invokers reach the header helper without the envelope; the request
    /// id travels as the request activity's baggage (set by TaskInvokeHandler). A mapping value
    /// still wins over it.
    /// </summary>
    [Theory]
    [InlineData(null, "vnext-rid")]
    [InlineData("""{"X-Request-ID":"mapping-rid"}""", "mapping-rid")]
    public async Task InvokeAsync_RequestIdFromBaggage_FillsOnlyWhenMappingIsAbsent(string? headers, string expected)
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);

        using var activity = new System.Diagnostics.Activity("execution-request").Start();
        activity.SetBaggage("x_request_id", "vnext-rid");

        await invoker.InvokeAsync(CreateDescriptor(headers: headers));

        handler.RequestHeaderValues("X-Request-Id").ShouldBe([expected]);
    }

    private static HttpTaskInvoker CreateInvoker(CapturingHttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<HttpTaskInvoker>.Instance);

    private static TaskDescriptor<HttpTaskBinding> CreateDescriptor(string? headers) =>
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

        public string[] RequestHeaderValues(string name) =>
            _requestHeaders is not null && _requestHeaders.NonValidated.TryGetValues(name, out var values)
                ? values.ToArray()
                : [];

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
