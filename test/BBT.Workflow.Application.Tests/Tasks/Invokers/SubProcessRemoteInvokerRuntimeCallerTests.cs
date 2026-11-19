using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.RuntimeCaller;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Tasks.Invokers;

/// <summary>
/// A cross-domain SubProcess start (caller C2, vnext#1265) carries a runtime-caller token for the
/// child's sub-start route on both transports, minted last so a mapping header cannot replace it.
/// </summary>
public sealed class SubProcessRemoteInvokerRuntimeCallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_ShouldCarryATokenForTheChildSubStartRoute(bool useDapr)
    {
        var (handler, identity) = (new CapturingHandler(), Identity(DefaultRing(), domain: "core"));

        await InvokeAsync(handler, useDapr, identity, mappingHeaders: new() { [RuntimeCallerToken.HeaderName] = "forged" });

        var token = handler.Request!.Headers.GetValues(RuntimeCallerToken.HeaderName).Single();
        var verdict = identity.Verify(token, RuntimeCallTarget.SubStart("partner", "flow"));
        verdict.IsValid.ShouldBeTrue();
        verdict.CallerClass.ShouldBe(RuntimeCallerClasses.SubProcess);
        verdict.CallerDomain.ShouldBe("core");
    }

    [Fact]
    public async Task Start_WithoutSigner_ShouldSendNoToken_EvenIfAMappingSetOne()
    {
        var handler = new CapturingHandler();

        await InvokeAsync(handler, useDapr: false, signer: null,
            mappingHeaders: new() { [RuntimeCallerToken.HeaderName] = "forged" });

        handler.Request!.Headers.Contains(RuntimeCallerToken.HeaderName).ShouldBeFalse();
    }

    private static Task<BBT.Workflow.Execution.TaskInvocationResult> InvokeAsync(
        CapturingHandler handler,
        bool useDapr,
        IRuntimeCallerSigner? signer,
        Dictionary<string, string> mappingHeaders)
    {
        var invoker = new SubProcessRemoteInvoker(
            new DaprServiceInvocationClient(new HttpClient(handler)),
            new FakeHttpClientFactory(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [VNextAppIds.ConfigKeys.AppDomain] = "core"
            }).Build(),
            NullLogger<SubProcessRemoteInvoker>.Instance,
            runtimeCallerSigner: signer);

        return invoker.InvokeAsync(new TaskDescriptor<SubProcessBinding>
        {
            TaskType = TaskTypes.SubProcess,
            TaskKey = "sub",
            Binding = new SubProcessBinding
            {
                Domain = "partner", Workflow = "flow", Version = "1", InstanceId = Guid.NewGuid(),
                BaseUrl = "https://partner.local", UseDapr = useDapr, DaprAppId = useDapr ? "partner-orchestration" : null,
                Headers = JsonSerializer.Serialize(mappingHeaders)
            }
        });
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"a","status":"A"}""")
            });
        }
    }
}
