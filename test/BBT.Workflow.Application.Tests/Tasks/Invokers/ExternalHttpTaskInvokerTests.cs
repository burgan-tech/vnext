using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

/// <summary>
/// Pins the in-process HTTP invoker to the Execution service's HttpTaskInvoker semantics:
/// same client-name selection, header/Content-Type handling, response parsing and
/// accepted-status-code matching, so output mappings observe identical shapes whichever
/// host performs the call.
/// </summary>
public sealed class ExternalHttpTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_SuccessfulJsonResponse_ReturnsParsedResult()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"orderId": 42}""")
        });
        handler.ResponseHeaders["X-Correlation"] = "abc";
        var factory = new CapturingHttpClientFactory(handler);
        var invoker = CreateInvoker(factory);

        var result = await invoker.InvokeAsync("local-call", CreateBinding());

        result.IsSuccess.ShouldBeTrue();
        result.StatusCode.ShouldBe(200);
        result.Body.ShouldBe("""{"orderId": 42}""");
        var data = (JsonElement)result.Data!;
        data.GetProperty("orderId").GetInt32().ShouldBe(42);
        result.Headers!.ShouldContainKey("X-Correlation");
        result.TaskType.ShouldBe("ExternalHttp");
        result.Metadata!["Url"].ShouldBe("https://workflow.local/endpoint");
    }

    [Fact]
    public async Task InvokeAsync_ErrorResponse_ReturnsFailureWithFullResponseDetails()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error": "boom"}""")
        });
        var invoker = CreateInvoker(new CapturingHttpClientFactory(handler));

        var result = await invoker.InvokeAsync("local-call", CreateBinding());

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(500);
        // The full response still flows to the output mapping / error boundary, like the remote path.
        result.Body.ShouldBe("""{"error": "boom"}""");
        result.Data.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task InvokeAsync_AcceptedStatusCode_OverridesFailure()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("degraded")
        });
        var invoker = CreateInvoker(new CapturingHttpClientFactory(handler));

        var result = await invoker.InvokeAsync(
            "local-call", CreateBinding(acceptedStatusCodes: ["5xx"]));

        result.IsSuccess.ShouldBeTrue();
        result.StatusCode.ShouldBe(503);
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_SelectsClientByValidateSsl_AndAppliesTaskTimeout()
    {
        var handler = new StubHttpMessageHandler(Ok());
        var factory = new CapturingHttpClientFactory(handler);
        var invoker = CreateInvoker(factory);

        await invoker.InvokeAsync("local-call", CreateBinding(validateSsl: false, timeoutSeconds: 7));

        factory.LastRequestedName.ShouldBe(WorkflowHttpClientNames.NoSslValidation);
        factory.LastCreatedClient!.Timeout.ShouldBe(TimeSpan.FromSeconds(7));

        await invoker.InvokeAsync("local-call", CreateBinding(validateSsl: true));
        factory.LastRequestedName.ShouldBe(WorkflowHttpClientNames.Default);
    }

    [Fact]
    public async Task InvokeAsync_GetRequest_DoesNotSendBody()
    {
        var handler = new StubHttpMessageHandler(Ok());
        var invoker = CreateInvoker(new CapturingHttpClientFactory(handler));

        await invoker.InvokeAsync("local-call", CreateBinding(method: "GET", body: """{"a":1}"""));

        handler.LastRequest!.Content.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_ContentTypeHeader_AppliesToContentNotRequestHeaders()
    {
        var handler = new StubHttpMessageHandler(Ok());
        var invoker = CreateInvoker(new CapturingHttpClientFactory(handler));

        await invoker.InvokeAsync("local-call", CreateBinding(
            method: "POST",
            body: "grant_type=client_credentials",
            headers: """{"Content-Type":"application/x-www-form-urlencoded","Authorization":"Bearer x"}"""));

        handler.LastContentType.ShouldBe("application/x-www-form-urlencoded");
        handler.LastRequest!.Headers.NonValidated.Contains("Authorization").ShouldBeTrue();
        handler.LastRequest.Headers.NonValidated.Contains("Content-Type").ShouldBeFalse();
    }

    [Fact]
    public async Task InvokeAsync_TransportFailure_ReturnsFailureResultInsteadOfThrowing()
    {
        var handler = new StubHttpMessageHandler(new HttpRequestException("connection refused"));
        var invoker = CreateInvoker(new CapturingHttpClientFactory(handler));

        var result = await invoker.InvokeAsync("local-call", CreateBinding());

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBeNull();
        result.ErrorMessage.ShouldBe("connection refused");
        result.Metadata!["ExceptionType"].ShouldBe(nameof(HttpRequestException));
    }

    [Fact]
    public async Task InvokeAsync_Cancellation_ReturnsCancelledFailure()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHttpMessageHandler(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });
        var invoker = CreateInvoker(new CapturingHttpClientFactory(handler));

        var result = await invoker.InvokeAsync("local-call", CreateBinding(), cts.Token);

        result.IsSuccess.ShouldBeFalse();
        result.Metadata!["Cancelled"].ShouldBe(true);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private static ExternalHttpTaskInvoker CreateInvoker(IHttpClientFactory factory) =>
        new(factory, NullLogger<ExternalHttpTaskInvoker>.Instance);

    private static HttpResponseMessage Ok() =>
        new(HttpStatusCode.OK) { Content = new StringContent("{}") };

    private static HttpTaskBinding CreateBinding(
        string method = "POST",
        string? body = null,
        string? headers = null,
        bool validateSsl = true,
        int timeoutSeconds = 30,
        IReadOnlyList<string>? acceptedStatusCodes = null) =>
        new()
        {
            Url = "https://workflow.local/endpoint",
            Method = method,
            Body = body,
            Headers = headers,
            ValidateSSL = validateSsl,
            TimeoutSeconds = timeoutSeconds,
            AcceptedStatusCodes = acceptedStatusCodes
        };

    private sealed class CapturingHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastRequestedName { get; private set; }
        public HttpClient? LastCreatedClient { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastRequestedName = name;
            LastCreatedClient = new HttpClient(handler, disposeHandler: false);
            return LastCreatedClient;
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastContentType { get; private set; }
        public Dictionary<string, string> ResponseHeaders { get; } = [];

        public StubHttpMessageHandler(HttpResponseMessage response)
            : this(_ => response)
        {
        }

        public StubHttpMessageHandler(Exception exception)
            : this(_ => throw exception)
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;

            var response = _responder(request);
            foreach (var header in ResponseHeaders)
            {
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return Task.FromResult(response);
        }
    }
}
