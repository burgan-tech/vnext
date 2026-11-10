using System;
using System.Net;
using System.Net.Http;
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
/// The remote start response carries the child's resolved <c>flowVersion</c>; the invoker reports it
/// under <see cref="SubProcessMetadataKeys.ResolvedFlowVersion"/> on both transports. A runtime that
/// sends none (or a body that is not an object) leaves the key out.
/// </summary>
public sealed class SubProcessRemoteInvokerFlowVersionTests
{
    [Theory]
    [InlineData(false, """{"id":"a","status":"A","flowVersion":"1.1.0"}""", "1.1.0")]
    [InlineData(true, """{"id":"a","status":"A","flowVersion":"1.1.0"}""", "1.1.0")]
    [InlineData(false, """{"id":"a","status":"A"}""", null)]
    [InlineData(true, """{"id":"a","status":"A"}""", null)]
    [InlineData(false, """{"flowVersion":42}""", null)]
    [InlineData(false, """{"flowVersion":""}""", null)]
    [InlineData(false, "not json", null)]
    [InlineData(true, "[1,2]", null)]
    [InlineData(false, "", null)]
    public async Task Success_ReportsResolvedVersionOnlyWhenBodyHasIt(bool useDapr, string body, string? expected)
    {
        var result = await InvokeAsync(useDapr, HttpStatusCode.OK, body);

        result.IsSuccess.ShouldBeTrue();
        if (expected is null)
            result.Metadata!.ContainsKey(SubProcessMetadataKeys.ResolvedFlowVersion).ShouldBeFalse();
        else
            result.Metadata![SubProcessMetadataKeys.ResolvedFlowVersion].ShouldBe(expected);
        result.Metadata!["Domain"].ShouldBe("partner");
    }

    [Fact]
    public async Task Failure_DoesNotReportVersion()
    {
        var result = await InvokeAsync(false, HttpStatusCode.InternalServerError, """{"flowVersion":"1.1.0"}""");

        result.IsSuccess.ShouldBeFalse();
        result.Metadata!.ContainsKey(SubProcessMetadataKeys.ResolvedFlowVersion).ShouldBeFalse();
    }

    private static async Task<BBT.Workflow.Execution.TaskInvocationResult> InvokeAsync(bool useDapr, HttpStatusCode status, string body)
    {
        var handler = new FixedHandler(status, body);
        var invoker = new SubProcessRemoteInvoker(
            new DaprServiceInvocationClient(new HttpClient(handler)),
            new FakeHttpClientFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                [VNextAppIds.ConfigKeys.AppDomain] = "core"
            }).Build(),
            NullLogger<SubProcessRemoteInvoker>.Instance);

        return await invoker.InvokeAsync(new TaskDescriptor<SubProcessBinding>
        {
            TaskType = TaskTypes.SubProcess,
            TaskKey = "sub",
            Binding = new SubProcessBinding
            {
                Domain = "partner", Workflow = "flow", Version = "1", InstanceId = Guid.NewGuid(),
                BaseUrl = "https://partner.local", UseDapr = useDapr, DaprAppId = useDapr ? "partner-orchestration" : null
            }
        });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
