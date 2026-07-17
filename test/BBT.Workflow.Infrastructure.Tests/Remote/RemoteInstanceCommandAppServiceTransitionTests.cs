using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

public sealed class RemoteInstanceCommandAppServiceTransitionTests
{
    [Fact]
    public async Task TransitionAsync_WithoutTermination_PreservesRawTransitionBody()
    {
        var handler = new RecordingHandler();
        var sut = CreateSut(handler);
        using var document = JsonDocument.Parse("""{"amount":42}""");
        var input = new TransitionInput(
            "child-domain",
            "child-flow",
            new TransitionDataInput(document.RootElement.Clone()));

        await sut.TransitionAsync(Guid.NewGuid(), "cancel", input, CancellationToken.None);

        handler.Request.ShouldNotBeNull();
        handler.Request.Headers.Contains(InternalTransitionEnvelope.HeaderName).ShouldBeFalse();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("attributes").GetProperty("amount").GetInt32().ShouldBe(42);
        body.RootElement.TryGetProperty("termination", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task TransitionAsync_WithTermination_SendsTypedInternalEnvelope()
    {
        var handler = new RecordingHandler();
        var sut = CreateSut(handler);
        var initiator = Guid.NewGuid();
        var cascade = Guid.NewGuid();
        var input = new TransitionInput("child-domain", "child-flow")
        {
            Termination = new TerminationContext(
                TerminationOrigin.ParentCascade,
                initiator,
                cascade)
        };

        await sut.TransitionAsync(Guid.NewGuid(), "cancel", input, CancellationToken.None);

        handler.Request.ShouldNotBeNull();
        handler.Request.Headers.GetValues(InternalTransitionEnvelope.HeaderName).Single()
            .ShouldBe(InternalTransitionEnvelope.HeaderValue);
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("data").ValueKind.ShouldBe(JsonValueKind.Null);
        var termination = body.RootElement.GetProperty("termination");
        termination.GetProperty("origin").GetString().ShouldBe("parentCascade");
        termination.GetProperty("initiatorInstanceId").GetGuid().ShouldBe(initiator);
        termination.GetProperty("cascadeId").GetGuid().ShouldBe(cascade);
    }

    private static RemoteInstanceCommandAppService CreateSut(RecordingHandler handler)
    {
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver.GetEndpointAsync("child-domain", EndpointKind.Url, Arg.Any<CancellationToken>())
            .Returns(Result<DiscoveryEndpoint>.Ok(
                new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://child.example/"))));

        return new RemoteInstanceCommandAppService(
            new HttpClient(handler),
            Options.Create(new RemoteOptions()),
            resolver,
            Substitute.For<ICurrentUser>());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
