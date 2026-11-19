using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Workers.Inbox.Forwarding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// The Inbox completion backup (C4, vnext#1265) carries no user, so only a runtime-caller token lets
/// it through a gated <c>complete</c>; and a gate refusal is re-delivered instead of being dropped
/// like every other 4xx.
/// </summary>
public sealed class DaprOrchestrationForwarderRuntimeCallerTests
{
    private const string GateRefusal = """{"status":403,"code":"Authorization:110001","detail":"refused"}""";
    private static readonly Guid Parent = Guid.NewGuid();
    private static readonly RuntimeCallTarget Complete = RuntimeCallTarget.Complete("core", "parent-flow", Parent);

    [Fact]
    public async Task Forward_WithTarget_ShouldCarryATokenForThatTarget()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "");
        var identity = Identity(DefaultRing(), domain: "core");

        await Forwarder(handler, identity).ForwardAsync(HttpMethod.Post, Route, new { }, "core", "parent-flow", null,
            Parent, CancellationToken.None, Complete);

        var verdict = identity.Verify(handler.Request!.Headers.GetValues(RuntimeCallerToken.HeaderName).Single(), Complete);
        verdict.IsValid.ShouldBeTrue();
        verdict.CallerClass.ShouldBe(RuntimeCallerClasses.Inbox);
    }

    [Fact]
    public async Task Forward_WithoutTarget_ShouldCarryNoToken()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "");

        await Forwarder(handler, Identity(DefaultRing())).ForwardAsync(HttpMethod.Post, "api/v1/core/x/sub/state", new { },
            "core", "parent-flow", null, Parent, CancellationToken.None);

        handler.Request!.Headers.Contains(RuntimeCallerToken.HeaderName).ShouldBeFalse();
    }

    [Fact]
    public async Task Forward_RefusedByTheGate_ShouldThrow_SoTheInboxRedelivers()
    {
        var handler = new CapturingHandler(HttpStatusCode.Forbidden, GateRefusal);

        var exception = await Should.ThrowAsync<HttpRequestException>(() =>
            Forwarder(handler, Identity(null)).ForwardAsync(HttpMethod.Post, Route, new { }, "core", "parent-flow", null,
                Parent, CancellationToken.None, Complete));

        exception.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Forward_Unsigned_RefusedWithTheGateCode_ShouldStillBeDropped()
    {
        // Only a signed forward is a gate refusal worth re-delivering; any other route keeps the drop.
        var handler = new CapturingHandler(HttpStatusCode.Forbidden, GateRefusal);

        await Should.NotThrowAsync(() =>
            Forwarder(handler, Identity(DefaultRing())).ForwardAsync(HttpMethod.Post, "api/v1/core/x/sub/state", new { },
                "core", "parent-flow", null, Parent, CancellationToken.None));
    }

    [Fact]
    public void CompleteRoute_ShouldBeBuiltFromTheSameValuesTheTokenIsBoundTo()
    {
        var (route, target) = GatedOrchestrationRoutes.Complete("core", "parent-flow", Parent);

        var segments = route.Split('/');
        segments.ShouldBe(["api", "v1", "core", "workflows", "parent-flow", "instances", Parent.ToString("D"), "complete"]);
        target.ShouldBe(RuntimeCallTarget.Complete(segments[2], segments[4], segments[6]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"code":"Authorization:110004"}""")]
    [InlineData(HttpStatusCode.Forbidden, "not json")]
    [InlineData(HttpStatusCode.BadRequest, GateRefusal)]
    [InlineData(HttpStatusCode.NotFound, "")]
    public async Task Forward_OtherNonTransientErrors_ShouldStillBeDropped(HttpStatusCode status, string body)
    {
        var handler = new CapturingHandler(status, body);

        await Should.NotThrowAsync(() =>
            Forwarder(handler, Identity(null)).ForwardAsync(HttpMethod.Post, Route, new { }, "core", "parent-flow", null,
                Parent, CancellationToken.None, Complete));
    }

    private static string Route => $"api/v1/core/workflows/parent-flow/instances/{Parent}/complete";

    private static DaprOrchestrationForwarder Forwarder(HttpMessageHandler handler, IRuntimeCallerSigner signer)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://orchestration.local/") }, "vnext-core-app", 30,
            NullLogger<DaprOrchestrationForwarder>.Instance, runtimeCallerSigner: signer);

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
