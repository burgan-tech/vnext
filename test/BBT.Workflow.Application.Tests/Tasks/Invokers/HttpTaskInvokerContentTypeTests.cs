using System;
using System.Diagnostics;
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

public sealed class HttpTaskInvokerContentTypeTests
{
    [Fact]
    public async Task InvokeAsync_WithoutContentType_DefaultsToApplicationJson()
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);

        await invoker.InvokeAsync(CreateDescriptor(body: "{\"a\":1}"));

        handler.CapturedContentType.ShouldBe("application/json");
        handler.CapturedBody.ShouldBe("{\"a\":1}");
    }

    [Fact]
    public async Task InvokeAsync_WithContentTypeHeader_AppliesToContentNotRequestHeaders()
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);

        var descriptor = CreateDescriptor(
            body: "grant_type=client_credentials",
            headers: """{"Content-Type":"application/x-www-form-urlencoded","Authorization":"Bearer x"}""");

        await invoker.InvokeAsync(descriptor);

        handler.CapturedContentType.ShouldBe("application/x-www-form-urlencoded");
        handler.CapturedBody.ShouldBe("grant_type=client_credentials");
        // Content-Type must not have leaked into request headers; Authorization should still be there.
        handler.RequestHeaderContains("Content-Type").ShouldBeFalse();
        handler.RequestHeaderContains("Authorization").ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeAsync_ExplicitContentType_OverridesHeader()
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);

        var descriptor = CreateDescriptor(
            body: "a=1&b=2",
            headers: """{"Content-Type":"text/plain"}""",
            contentType: "application/x-www-form-urlencoded");

        await invoker.InvokeAsync(descriptor);

        handler.CapturedContentType.ShouldBe("application/x-www-form-urlencoded");
    }

    [Fact]
    public async Task InvokeAsync_WithWorkflowBaggage_OverridesMappingCorrelationHeaders()
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);
        var instanceId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        using var activity = new Activity("http-task-test").Start();
        activity.SetBaggage("workflow.instance.id", instanceId.ToString("D"));
        activity.SetBaggage("correlation.id", correlationId.ToString("N"));
        activity.SetBaggage("sub", "12345678901");
        activity.SetBaggage("act.sub", "U0B006");

        await invoker.InvokeAsync(CreateDescriptor(
            body: "{}",
            headers: """{"X-Workflow-Instance-Id":"spoofed","X-Correlation-Id":"spoofed","sub":"spoofed.value","act_sub":"spoofed.value"}"""));

        handler.GetRequestHeader("X-Workflow-Instance-Id")
            .ShouldBe(instanceId.ToString("D").ToLowerInvariant());
        handler.GetRequestHeader("X-Correlation-Id")
            .ShouldBe(correlationId.ToString("N"));
        handler.GetRequestHeader("sub").ShouldBe("12345678901");
        handler.GetRequestHeader("act_sub").ShouldBe("U0B006");
    }

    [Fact]
    public async Task InvokeAsync_WithoutValidWorkflowBaggage_RemovesMappingCorrelationHeaders()
    {
        var handler = new CapturingHttpMessageHandler();
        var invoker = CreateInvoker(handler);

        await invoker.InvokeAsync(CreateDescriptor(
            body: "{}",
            headers: """{"X-Workflow-Instance-Id":"spoofed","X-Correlation-Id":"spoofed","sub":"spoofed.value","act_sub":"spoofed.value"}"""));

        handler.RequestHeaderContains("X-Workflow-Instance-Id").ShouldBeFalse();
        handler.RequestHeaderContains("X-Correlation-Id").ShouldBeFalse();
        handler.RequestHeaderContains("sub").ShouldBeFalse();
        handler.RequestHeaderContains("act_sub").ShouldBeFalse();
    }

    private static HttpTaskInvoker CreateInvoker(CapturingHttpMessageHandler handler) =>
        new(new FakeHttpClientFactory(handler), NullLogger<HttpTaskInvoker>.Instance);

    private static TaskDescriptor<HttpTaskBinding> CreateDescriptor(
        string body,
        string? headers = null,
        string? contentType = null) =>
        new()
        {
            TaskType = TaskTypes.Http,
            TaskKey = "http-task",
            Binding = new HttpTaskBinding
            {
                Url = "https://workflow.local/endpoint",
                Method = "POST",
                Body = body,
                Headers = headers,
                ContentType = contentType
            }
        };

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private HttpRequestHeaders? _requestHeaders;

        public string? CapturedContentType { get; private set; }
        public string? CapturedBody { get; private set; }

        // NonValidated allows querying any header name (including content-header names) without throwing.
        public bool RequestHeaderContains(string name) =>
            _requestHeaders?.NonValidated.Contains(name) ?? false;

        public string? GetRequestHeader(string name) =>
            _requestHeaders?.NonValidated.TryGetValues(name, out var values) == true
                ? values.ToString()
                : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestHeaders = request.Headers;
            if (request.Content != null)
            {
                CapturedContentType = request.Content.Headers.ContentType?.MediaType;
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
