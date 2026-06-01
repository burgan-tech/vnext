using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class HttpTaskInvokerAcceptedStatusCodesTests
{
    [Fact]
    public async Task InvokeAsync_WithAcceptedExact400_ReturnsSuccessWithResponseDetails()
    {
        var invoker = CreateInvoker(HttpStatusCode.BadRequest);
        var descriptor = CreateDescriptor(["400"]);

        var result = await invoker.InvokeAsync(descriptor);

        result.IsSuccess.ShouldBeTrue();
        result.StatusCode.ShouldBe(400);
        result.Body.ShouldContain("Validation failed");
        result.Headers.ShouldNotBeNull();
        result.Headers!["x-validation-source"].ShouldBe("schema");
        result.Data.ShouldNotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_WithUnaccepted400_ReturnsFailure()
    {
        var invoker = CreateInvoker(HttpStatusCode.BadRequest);
        var descriptor = CreateDescriptor(null);

        var result = await invoker.InvokeAsync(descriptor);

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        result.Body.ShouldContain("Validation failed");
        result.Data.ShouldNotBeNull();
    }

    [Fact]
    public async Task InvokeAsync_WithAcceptedWildcard4xx_ReturnsSuccess()
    {
        var invoker = CreateInvoker(HttpStatusCode.BadRequest);
        var descriptor = CreateDescriptor(["4xx"]);

        var result = await invoker.InvokeAsync(descriptor);

        result.IsSuccess.ShouldBeTrue();
        result.StatusCode.ShouldBe(400);
    }

    private static HttpTaskInvoker CreateInvoker(HttpStatusCode statusCode)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("""
                {
                  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                  "title": "Validation failed",
                  "status": 400,
                  "errors": {
                    "$.phoneNumber": [ "Phone number is required." ]
                  }
                }
                """)
        };
        response.Headers.TryAddWithoutValidation("x-validation-source", "schema");

        return new HttpTaskInvoker(
            new FakeHttpClientFactory(response),
            NullLogger<HttpTaskInvoker>.Instance);
    }

    private static TaskDescriptor<HttpTaskBinding> CreateDescriptor(IReadOnlyList<string>? acceptedStatusCodes) =>
        new()
        {
            TaskType = TaskTypes.Http,
            TaskKey = "send-otp",
            Binding = new HttpTaskBinding
            {
                Url = "https://workflow.local/functions/send-otp",
                Method = "POST",
                Body = "{}",
                AcceptedStatusCodes = acceptedStatusCodes
            }
        };

    private sealed class FakeHttpClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHttpMessageHandler(response));
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(response);
        }
    }
}
