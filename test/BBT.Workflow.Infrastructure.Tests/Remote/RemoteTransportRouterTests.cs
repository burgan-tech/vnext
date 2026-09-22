using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
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
/// Pins that the routing signal is <see cref="DiscoveryEndpoint.Kind"/> and nothing else, that a
/// Dapr endpoint on a host without a registered Dapr shell fails as a transport error rather than a DI
/// error, and that a genuinely unreachable endpoint is reported so the discovery cache can drop it.
/// </summary>
/// <remarks>
/// The reporting tests carry weight beyond this class: with the discovery cache holding no expiry,
/// this catch block is the only automatic way a moved domain is ever corrected for a runtime that is
/// not being deployed. The negative cases matter just as much — reporting an HTTP error response or a
/// TLS failure would put the registry in the path of every downstream bug.
/// </remarks>
public sealed class RemoteTransportRouterTests
{
    public sealed class Probe;

    [Fact]
    public async Task Url_Kind_Should_Route_To_Http()
    {
        var http = new RecordingHandler();
        var router = new RemoteTransportRouter<Probe>(
            new HttpRemoteTransport<Probe>(new HttpClient(http)),
            new NullDiscoveryEndpointFeedback(),
            new ServiceCollection().BuildServiceProvider());

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
            new HttpRemoteTransport<Probe>(new HttpClient(http)),
            new NullDiscoveryEndpointFeedback(),
            services.BuildServiceProvider());
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
            new NullDiscoveryEndpointFeedback(),
            new ServiceCollection().BuildServiceProvider());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => router.SendAsync(
            new DiscoveryEndpoint(EndpointKind.Dapr, new Uri("dapr://app/"), "app"),
            HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        ex.Message.ShouldContain("no Dapr transport");
    }


    // ────────────────────────────────────────────────────────────────────
    // Unreachable-endpoint reporting
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_connection_failure_reports_the_endpoint_as_unreachable()
    {
        var feedback = Substitute.For<IDiscoveryEndpointFeedback>();
        var router = BuildRouter(feedback, _ => throw new HttpRequestException(
            "refused", new SocketException((int)SocketError.ConnectionRefused)));

        await Should.ThrowAsync<HttpRequestException>(() => router.SendAsync(
            Endpoint(), HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        await feedback.Received(1).ReportUnreachableAsync(
            "lending", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_dns_failure_reports_the_endpoint_as_unreachable()
    {
        var feedback = Substitute.For<IDiscoveryEndpointFeedback>();
        var router = BuildRouter(feedback, _ => throw new HttpRequestException(
            HttpRequestError.NameResolutionError, "no such host"));

        await Should.ThrowAsync<HttpRequestException>(() => router.SendAsync(
            Endpoint(), HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        await feedback.Received(1).ReportUnreachableAsync(
            "lending", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task An_error_RESPONSE_never_reports_the_endpoint_as_unreachable(HttpStatusCode status)
    {
        var feedback = Substitute.For<IDiscoveryEndpointFeedback>();
        var router = BuildRouter(feedback, _ => new HttpResponseMessage(status));

        await router.SendAsync(Endpoint(), HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None);

        // A domain answering with an error is a domain at the right address. Evicting here would make
        // every downstream bug look like a discovery problem.
        await feedback.DidNotReceive().ReportUnreachableAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_tls_failure_does_not_report_the_endpoint_as_unreachable()
    {
        var feedback = Substitute.For<IDiscoveryEndpointFeedback>();
        var router = BuildRouter(feedback, _ => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError, "handshake failed"));

        await Should.ThrowAsync<HttpRequestException>(() => router.SendAsync(
            Endpoint(), HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        // Far more often a certificate problem at the right address than a stranger answering on a
        // moved one — and a lookup per failed handshake would fix none of them.
        await feedback.DidNotReceive().ReportUnreachableAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_dapr_shell_does_not_report_the_endpoint_as_unreachable()
    {
        var feedback = Substitute.For<IDiscoveryEndpointFeedback>();
        var router = new RemoteTransportRouter<Probe>(
            new HttpRemoteTransport<Probe>(new HttpClient(new RecordingHandler())),
            feedback,
            new ServiceCollection().BuildServiceProvider());

        await Should.ThrowAsync<HttpRequestException>(() => router.SendAsync(
            new DiscoveryEndpoint(EndpointKind.Dapr, new Uri("dapr://app/"), "app", "lending"),
            HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        // A configuration error wearing HttpRequestException's clothes, so the callers' contract
        // holds. Nothing about the cached address is wrong.
        await feedback.DidNotReceive().ReportUnreachableAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static DiscoveryEndpoint Endpoint() =>
        new(EndpointKind.Url, new Uri("https://remote.test/"), null, "lending");

    private static RemoteTransportRouter<Probe> BuildRouter(
        IDiscoveryEndpointFeedback feedback,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(
            new HttpRemoteTransport<Probe>(new HttpClient(new StubHandler(respond))),
            feedback,
            new ServiceCollection().BuildServiceProvider());

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
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
