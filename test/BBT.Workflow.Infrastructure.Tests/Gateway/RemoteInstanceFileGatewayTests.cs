using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Discovery;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Files;
using BBT.Workflow.Gateway;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

/// <summary>
/// Covers <see cref="RemoteInstanceFileGateway"/> over a fake <see cref="IRemoteTransport{TClient}"/>: the request
/// shape (route, file query, Accept), the handle read from <c>X-File-Handle</c>, raw-byte bodies, non-success mapping
/// and the 64 MiB cap.
/// </summary>
public sealed class RemoteInstanceFileGatewayTests
{
    private static readonly FileHandle Handle = new(
        "vnext-blob-local", "f 1/x", "p.pdf", "application/pdf", 3, "abc", new FileOwner("partner", "kyc", "i-1"));

    private static readonly byte[] Bytes = [0xFF, 0x00, 0x7F];

    private sealed class FakeTransport(Func<HttpResponseMessage> respond) : IRemoteTransport<RemoteInstanceFileGateway>
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Paths { get; } = [];

        public Task<HttpResponseMessage> SendAsync(
            DiscoveryEndpoint endpoint, HttpMethod method, string relativePath,
            Action<HttpRequestMessage>? configure, CancellationToken cancellationToken)
        {
            var request = new HttpRequestMessage(method, new Uri(endpoint.BaseUrl, relativePath.TrimStart('/')));
            request.Headers.Accept.ParseAdd("application/json"); // what the named client's default would add
            configure?.Invoke(request);
            Requests.Add(request);
            Paths.Add(relativePath);
            return Task.FromResult(respond());
        }
    }

    private static (RemoteInstanceFileGateway Sut, FakeTransport Transport) Create(Func<HttpResponseMessage> respond)
    {
        var transport = new FakeTransport(respond);
        var resolver = Substitute.For<IDomainDiscoveryResolver>();
        resolver.GetEndpointAsync(Arg.Any<string>(), Arg.Any<EndpointKind>(), Arg.Any<CancellationToken>())
            .Returns(ci => Result<DiscoveryEndpoint>.Ok(
                new DiscoveryEndpoint(EndpointKind.Url, new Uri($"https://{ci.Arg<string>()}.test/"))));
        return (new RemoteInstanceFileGateway(transport, Options.Create(new RemoteOptions()), resolver), transport);
    }

    private static HttpResponseMessage Ok(byte[] body, string? handleHeader = null, long? contentLength = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        if (contentLength is { } length)
            response.Content.Headers.ContentLength = length;
        response.Headers.TryAddWithoutValidation(HeadersConstants.XFileHandle,
            handleHeader ?? Convert.ToBase64String(Encoding.UTF8.GetBytes(Handle.ToJsonNode().ToJsonString())));
        return response;
    }

    [Fact]
    public async Task Success_ReturnsRawBytesAndTheHandleFromTheHeader()
    {
        var (sut, transport) = Create(() => Ok(Bytes));

        var result = await sut.ReadAsync("partner", "kyc", "i-1", "f 1/x", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Bytes.ShouldBe(Bytes);
        result.Value.NotModified.ShouldBeFalse();
        result.Value.Handle.ShouldBe(Handle);

        transport.Paths.Single().ShouldEndWith("/partner/workflows/kyc/instances/i-1/internal/file?file=f%201%2Fx");
        var request = transport.Requests.Single();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Headers.Accept.Select(a => a.MediaType).ShouldBe(["*/*"]);
    }

    [Fact]
    public async Task NonSuccess_IsMappedToAnError()
    {
        var (sut, _) = Create(() => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("missing") });

        var result = await sut.ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("remote_not_found");
    }

    [Fact]
    public async Task OversizeContentLength_IsFileStoreUnavailable()
    {
        var (sut, _) = Create(() => Ok(Bytes, contentLength: RemoteInstanceFileGateway.MaxBytes + 1));

        var result = await sut.ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
        result.Error.Target.ShouldBe("partner");
    }

    [Fact]
    public async Task OversizeBodyWithoutContentLength_IsFileStoreUnavailable()
    {
        var big = new byte[RemoteInstanceFileGateway.MaxBytes + 1];
        var (sut, _) = Create(() =>
        {
            var response = Ok(big);
            response.Content.Headers.ContentLength = null;
            return response;
        });

        var result = await sut.ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("e30=")] // {} — not a handle
    public async Task UnreadableHandleHeader_IsFileStoreUnavailable(string header)
    {
        var (sut, _) = Create(() => Ok(Bytes, handleHeader: header));

        var result = await sut.ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Fact]
    public async Task TransportFailure_IsTransient()
    {
        var (sut, _) = Create(() => throw new HttpRequestException("down"));

        var result = await sut.ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None);

        result.Error.Code.ShouldBe("remote_network_error");
    }
}
