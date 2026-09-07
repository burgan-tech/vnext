using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using BBT.Workflow.Remote;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Verifies the conditional-header contract of the remote state-function call
/// (<see cref="RemoteInstanceQueryAppService.GetFunctionWithStateAsync"/>): the caller's own
/// If-None-Match (an ETag for a different resource — e.g. the parent instance during the
/// subflow window) must never be forwarded from the headers dictionary; only the typed
/// <c>input.IfNoneMatch</c> may produce a conditional request.
/// </summary>
public sealed class RemoteInstanceQueryStateCallHeaderTests
{
    private HttpRequestMessage? _capturedRequest;

    private RemoteInstanceQueryAppService CreateSut(HttpStatusCode responseStatus = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(request =>
        {
            _capturedRequest = request;
            var response = new HttpResponseMessage(responseStatus);
            if (responseStatus == HttpStatusCode.OK)
                response.Content = new StringContent("""{ "state": "review" }""");
            return response;
        });

        var endpointResolver = Substitute.For<IDomainDiscoveryResolver>();
        endpointResolver
            .GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(Result<DiscoveryEndpoint>.Ok(
                new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://remote-domain.test/"))));

        return new RemoteInstanceQueryAppService(
            new HttpRemoteTransport<IRemoteInstanceQueryAppService>(new HttpClient(handler)),
            Options.Create(new RemoteOptions()),
            endpointResolver,
            Substitute.For<ICurrentUser>());
    }

    private static GetFunctionWithInstanceInput CreateInput(
        string? ifNoneMatch = null,
        Dictionary<string, string?>? headers = null) => new()
    {
        Domain = "sub-domain",
        Workflow = "sub-flow",
        Instance = Guid.NewGuid().ToString(),
        IfNoneMatch = ifNoneMatch,
        Headers = headers ?? new Dictionary<string, string?>(),
        QueryParams = new Dictionary<string, string?>()
    };

    [Fact]
    public async Task GetFunctionWithStateAsync_DoesNotForwardCallersIfNoneMatchHeader()
    {
        var sut = CreateSut();
        var input = CreateInput(headers: new Dictionary<string, string?>
        {
            ["if-none-match"] = "\"parent-etag\"",
            ["accept-language"] = "tr-TR"
        });

        var result = await sut.GetFunctionWithStateAsync(input, CancellationToken.None);

        result.Result.IsSuccess.ShouldBeTrue();
        _capturedRequest.ShouldNotBeNull();
        _capturedRequest!.Headers.Contains("If-None-Match").ShouldBeFalse();
        _capturedRequest.Headers.GetValues("accept-language").Single().ShouldBe("tr-TR");
    }

    [Fact]
    public async Task GetFunctionWithStateAsync_SendsTypedIfNoneMatchExactlyOnce()
    {
        var sut = CreateSut();
        var input = CreateInput(
            ifNoneMatch: "\"intended-etag\"",
            headers: new Dictionary<string, string?> { ["if-none-match"] = "\"leaked-etag\"" });

        await sut.GetFunctionWithStateAsync(input, CancellationToken.None);

        _capturedRequest.ShouldNotBeNull();
        _capturedRequest!.Headers.GetValues("If-None-Match").Single().ShouldBe("\"intended-etag\"");
    }

    [Fact]
    public async Task GetFunctionWithStateAsync_MapsRemote304ToNotModified()
    {
        var sut = CreateSut(HttpStatusCode.NotModified);
        var input = CreateInput(ifNoneMatch: "\"intended-etag\"");

        var result = await sut.GetFunctionWithStateAsync(input, CancellationToken.None);

        result.IsNotModified.ShouldBeTrue();
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
