using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Tracing;
using BBT.Aether.Users;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Logging;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;
using FixedTimeProvider = BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys.FixedTimeProvider;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// The cross-domain SubFlow start (C1) and the cross-domain completion relay (C3) carry a
/// runtime-caller token for the peer's route (vnext#1265), stamped after the ambient user's and the
/// mapping's headers so neither can replace it. The relay often runs with no user at all.
/// </summary>
public sealed class RemoteInstanceCommandAppServiceRuntimeCallerTests
{
    [Fact]
    public async Task StartSub_ShouldCarryATokenForTheChildRoute_OverAMappingHeader()
    {
        var handler = new CapturingHandler();
        var identity = Identity(DefaultRing(), domain: "core");
        var input = new StartInstanceInput("child-domain", "child-flow")
        {
            Instance = new CreateInstanceInput(),
            Headers = new Dictionary<string, string?> { [RuntimeCallerToken.HeaderName] = "forged" }
        };

        (await CreateSut(handler, identity).StartSubAsync(input, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        var verdict = identity.Verify(Token(handler), RuntimeCallTarget.SubStart("child-domain", "child-flow"));
        verdict.IsValid.ShouldBeTrue();
        verdict.CallerClass.ShouldBe(RuntimeCallerClasses.SubFlow);
    }

    [Fact]
    public async Task Complete_ShouldCarryATokenForTheParentInstanceRoute()
    {
        var handler = new CapturingHandler();
        var identity = Identity(DefaultRing(), domain: "child-domain");
        var parent = Guid.NewGuid();

        var result = await CreateSut(handler, identity).CompleteAsync(new FlowCompletedInput
        {
            InstanceId = parent, Domain = "child-domain", Flow = "parent-flow", Version = null,
            SubInstanceId = Guid.NewGuid(), CompletedState = "done", CompletedAt = DateTime.UtcNow
        }, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var verdict = identity.Verify(Token(handler), RuntimeCallTarget.Complete("child-domain", "parent-flow", parent));
        verdict.IsValid.ShouldBeTrue();
        verdict.CallerClass.ShouldBe(RuntimeCallerClasses.Relay);
        identity.Verify(Token(handler), RuntimeCallTarget.Complete("child-domain", "parent-flow", Guid.NewGuid()))
            .IsValid.ShouldBeFalse();
    }

    [Fact]
    public async Task Complete_OverDapr_ShouldSignEveryAttemptAfresh()
    {
        // The Dapr transport retries through its policy and re-runs `configure` per attempt: each attempt
        // must carry its own current token, not a copy minted for the first.
        var clock = new FixedTimeProvider(Now);
        var identity = Identity(DefaultRing(), domain: "child-domain", clock: clock);
        var handler = new SequenceHandler(clock, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver.GetEndpointAsync("parent-domain", EndpointKind.Url, Arg.Any<CancellationToken>())
            .Returns(Result<DiscoveryEndpoint>.Ok(new DiscoveryEndpoint(
                EndpointKind.Dapr, new Uri("http://vnext-app-parent-domain"), "vnext-app-parent-domain")));
        var retryOnce = Policy.HandleResult<HttpResponseMessage>(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)
            .RetryAsync(1);
        var sut = new RemoteInstanceCommandAppService(
            new DaprRemoteTransport<IRemoteInstanceCommandAppService>(new HttpClient(handler), retryOnce),
            Options.Create(new RemoteOptions()), resolver, Substitute.For<ICurrentUser>(),
            Substitute.For<ICorrelationIdProvider>(), identity);
        var parent = Guid.NewGuid();

        (await sut.CompleteAsync(new FlowCompletedInput
        {
            InstanceId = parent, Domain = "parent-domain", Flow = "parent-flow", Version = null,
            SubInstanceId = Guid.NewGuid(), CompletedState = "done", CompletedAt = DateTime.UtcNow
        }, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        handler.Tokens.Count.ShouldBe(2);
        handler.Tokens[0].ShouldNotBe(handler.Tokens[1]);
        var target = RuntimeCallTarget.Complete("parent-domain", "parent-flow", parent);
        handler.Tokens.ShouldAllBe(token => identity.Verify(token, target).IsValid);
    }

    [Fact]
    public async Task StartSub_WithoutSigner_ShouldDropAnyTokenAMappingSet()
    {
        var handler = new CapturingHandler();
        var input = new StartInstanceInput("child-domain", "child-flow")
        {
            Instance = new CreateInstanceInput(),
            Headers = new Dictionary<string, string?> { [RuntimeCallerToken.HeaderName] = "forged" }
        };

        await CreateSut(handler, signer: null).StartSubAsync(input, CancellationToken.None);

        handler.Request!.Headers.Contains(RuntimeCallerToken.HeaderName).ShouldBeFalse();
    }

    private static string Token(CapturingHandler handler)
        => handler.Request!.Headers.GetValues(RuntimeCallerToken.HeaderName).Single();

    private static RemoteInstanceCommandAppService CreateSut(CapturingHandler handler, IRuntimeCallerSigner? signer)
    {
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver.GetEndpointAsync("child-domain", EndpointKind.Url, Arg.Any<CancellationToken>())
            .Returns(Result<DiscoveryEndpoint>.Ok(
                new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://child.example/"))));

        return new RemoteInstanceCommandAppService(
            new HttpRemoteTransport<IRemoteInstanceCommandAppService>(new HttpClient(handler)),
            Options.Create(new RemoteOptions()),
            resolver,
            Substitute.For<ICurrentUser>(),
            Substitute.For<ICorrelationIdProvider>(),
            signer);
    }

    private sealed class SequenceHandler(FixedTimeProvider clock, params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string> Tokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.GetValues(RuntimeCallerToken.HeaderName).Single());
            var status = statuses[Math.Min(Tokens.Count - 1, statuses.Length - 1)];
            clock.Value = clock.Value.AddSeconds(5);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"00000000-0000-0000-0000-000000000001","status":"A"}""")
            });
        }
    }
}
