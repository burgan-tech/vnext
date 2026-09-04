using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using BBT.Workflow.Remote;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins that the routing signal is <see cref="DiscoveryEndpoint.Kind"/> and nothing else, and that a
/// Dapr endpoint on a host without a registered Dapr shell fails as a transport error rather than a DI error.
/// </summary>
public sealed class RemoteTransportRouterTests
{
    public sealed class Probe;

    [Fact]
    public async Task Url_Kind_Should_Route_To_Http()
    {
        var http = new RecordingHandler();
        var router = new RemoteTransportRouter<Probe>(
            new HttpRemoteTransport<Probe>(new HttpClient(http)), new ServiceCollection().BuildServiceProvider());

        await router.SendAsync(new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://remote.test/")),
            HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None);

        http.LastUri!.ToString().ShouldBe("https://remote.test/api/v1.0/x");
    }

    [Fact]
    public async Task Dapr_Kind_Should_Route_To_The_Dapr_Shell()
    {
        var dapr = Substitute.For<IDaprRemoteTransport<Probe>>();
        dapr.SendAsync(Arg.Any<DiscoveryEndpoint>(), Arg.Any<HttpMethod>(), Arg.Any<string>(),
                Arg.Any<Action<HttpRequestMessage>?>(), Arg.Any<CancellationToken>())
            .Returns(new HttpResponseMessage(HttpStatusCode.OK));
        var services = new ServiceCollection();
        services.AddSingleton(dapr);
        var http = new RecordingHandler();
        var router = new RemoteTransportRouter<Probe>(
            new HttpRemoteTransport<Probe>(new HttpClient(http)), services.BuildServiceProvider());
        var endpoint = new DiscoveryEndpoint(EndpointKind.Dapr, new Uri("dapr://app/"), "app");

        await router.SendAsync(endpoint, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None);

        await dapr.Received(1).SendAsync(endpoint, HttpMethod.Get, "api/v1.0/x", null, Arg.Any<CancellationToken>());
        http.LastUri.ShouldBeNull();
    }

    /// <summary>
    /// The callers' contract is <c>catch (HttpRequestException)</c>; a host with no Dapr shell
    /// registered must land there, not surface a DI error or a <see cref="NotSupportedException"/>
    /// from HttpClient on a <c>dapr://</c> URI.
    /// </summary>
    [Fact]
    public async Task Dapr_Kind_Without_Registered_Dapr_Shell_Should_Throw_HttpRequestException()
    {
        var router = new RemoteTransportRouter<Probe>(
            new HttpRemoteTransport<Probe>(new HttpClient(new RecordingHandler())),
            new ServiceCollection().BuildServiceProvider());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => router.SendAsync(
            new DiscoveryEndpoint(EndpointKind.Dapr, new Uri("dapr://app/"), "app"),
            HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        ex.Message.ShouldContain("no Dapr transport");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
