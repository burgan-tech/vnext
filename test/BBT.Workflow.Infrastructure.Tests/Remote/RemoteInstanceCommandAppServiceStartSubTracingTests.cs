using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Tracing;
using BBT.Aether.Users;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Logging;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

public sealed class RemoteInstanceCommandAppServiceStartSubTracingTests
{
    [Fact]
    public async Task StartSubAsync_CarriesActivationEpisodeInInternalBody()
    {
        var handler = new RecordingHandler();
        var sut = CreateSut(handler);
        var startedAt = new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.Zero);
        var episode = new ActivationEpisode(
            startedAt,
            TelemetryConstants.ActivationTriggers.Start,
            "create",
            Partial: false);
        using var lane = WorkflowTraceLane.Use(
            "00-11111111111111111111111111111111-2222222222222222-01",
            episode: episode);
        var input = new StartInstanceInput("child-domain", "child-flow")
        {
            Instance = new CreateInstanceInput()
        };

        var result = await sut.StartSubAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("episodeStartedAt").GetDateTimeOffset().ShouldBe(startedAt);
        body.RootElement.GetProperty("episodeTrigger").GetString().ShouldBe("start");
        body.RootElement.GetProperty("episodeTransitionKey").GetString().ShouldBe("create");
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
            Substitute.For<ICurrentUser>(),
            Substitute.For<ICorrelationIdProvider>());
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
