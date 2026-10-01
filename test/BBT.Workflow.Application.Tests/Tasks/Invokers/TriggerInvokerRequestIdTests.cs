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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

/// <summary>
/// StartTrigger and SubProcess apply ONLY the X-Request-Id rule of the trusted correlation
/// headers: the mapping's non-empty value wins, otherwise the Execution host's request id
/// (the <c>x_request_id</c> baggage TaskInvokeHandler restores from the envelope) is sent. The
/// workflow-context and identity headers stay untouched on these calls, as before.
/// </summary>
public sealed class TriggerInvokerRequestIdTests
{
    [Theory]
    [InlineData(null, "vnext-rid")]
    [InlineData("""{"X-Request-Id":""}""", "vnext-rid")]
    [InlineData("""{"X-Request-ID":"mapping-rid"}""", "mapping-rid")]
    public async Task StartTrigger_FillsRequestIdOnlyWhenMappingIsAbsent(string? headers, string expected)
    {
        var handler = new CapturingHandler();
        var invoker = new StartTriggerRemoteInvoker(
            NewDaprClient(), new FakeHttpClientFactory(handler), Config(),
            NullLogger<StartTriggerRemoteInvoker>.Instance);

        using var activity = StartRequestActivity("vnext-rid");
        await invoker.InvokeAsync(new TaskDescriptor<StartTriggerBinding>
        {
            TaskType = TaskTypes.StartTrigger,
            TaskKey = "start",
            Binding = new StartTriggerBinding
            {
                Domain = "partner", Workflow = "flow", BaseUrl = "https://partner.local", Headers = headers
            }
        });

        handler.Values("X-Request-Id").ShouldBe([expected]);
    }

    [Theory]
    [InlineData(null, "vnext-rid")]
    [InlineData("""{"x-request-id":"mapping-rid"}""", "mapping-rid")]
    public async Task SubProcess_FillsRequestIdOnlyWhenMappingIsAbsent(string? headers, string expected)
    {
        var handler = new CapturingHandler();
        var invoker = new SubProcessRemoteInvoker(
            NewDaprClient(), new FakeHttpClientFactory(handler), Config(),
            NullLogger<SubProcessRemoteInvoker>.Instance);

        using var activity = StartRequestActivity("vnext-rid");
        await invoker.InvokeAsync(new TaskDescriptor<SubProcessBinding>
        {
            TaskType = TaskTypes.SubProcess,
            TaskKey = "sub",
            Binding = new SubProcessBinding
            {
                Domain = "partner", Workflow = "flow", InstanceId = Guid.NewGuid(),
                BaseUrl = "https://partner.local", Headers = headers
            }
        });

        handler.Values("X-Request-Id").ShouldBe([expected]);
    }

    /// <summary>Only X-Request-Id is added: no workflow-context or identity header appears.</summary>
    [Fact]
    public async Task StartTrigger_DoesNotStampTheOtherTrustedHeaders()
    {
        var handler = new CapturingHandler();
        var invoker = new StartTriggerRemoteInvoker(
            NewDaprClient(), new FakeHttpClientFactory(handler), Config(),
            NullLogger<StartTriggerRemoteInvoker>.Instance);

        using var activity = StartRequestActivity("vnext-rid");
        activity.SetBaggage("workflow.instance.id", Guid.NewGuid().ToString("D"));
        activity.SetBaggage("correlation.id", Guid.NewGuid().ToString("N"));
        activity.SetBaggage("sub", "user-1");

        await invoker.InvokeAsync(new TaskDescriptor<StartTriggerBinding>
        {
            TaskType = TaskTypes.StartTrigger,
            TaskKey = "start",
            Binding = new StartTriggerBinding { Domain = "partner", Workflow = "flow", BaseUrl = "https://partner.local" }
        });

        handler.Values("X-Request-Id").ShouldBe(["vnext-rid"]);
        handler.Values("X-Workflow-Instance-Id").ShouldBeEmpty();
        handler.Values("X-Correlation-Id").ShouldBeEmpty();
        handler.Values("sub").ShouldBeEmpty();
    }

    private static Activity StartRequestActivity(string requestId)
    {
        var activity = new Activity("execution-request").Start();
        activity.SetBaggage("x_request_id", requestId);
        return activity;
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                [VNextAppIds.ConfigKeys.AppDomain] = "core"
            })
            .Build();

    private static DaprServiceInvocationClient NewDaprClient() =>
        new(new HttpClient(new CapturingHandler()));

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private HttpRequestHeaders? _headers;

        public string[] Values(string name) =>
            _headers is not null && _headers.NonValidated.TryGetValues(name, out var values)
                ? values.ToArray()
                : [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _headers = request.Headers;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
