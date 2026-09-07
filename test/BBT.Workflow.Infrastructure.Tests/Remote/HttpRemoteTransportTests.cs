using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using BBT.Workflow.Remote;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

public sealed class HttpRemoteTransportTests
{
    private sealed class Probe;

    [Fact]
    public async Task Should_Compose_BaseUrl_And_Relative_Path_Preserving_Query()
    {
        var handler = new RecordingHandler();
        var sut = new HttpRemoteTransport<Probe>(new HttpClient(handler));

        await sut.SendAsync(new DiscoveryEndpoint(EndpointKind.Url, new Uri("https://remote.test/")),
            HttpMethod.Get, "/api/v1.0/x?filter=a%20b", null, CancellationToken.None);

        // AbsoluteUri, not ToString(): ToString() unescapes for display; AbsoluteUri is what reaches the wire.
        handler.Requests[0].RequestUri!.AbsoluteUri.ShouldBe("https://remote.test/api/v1.0/x?filter=a%20b");
    }

    /// <summary>
    /// Call sites build one <see cref="StringContent"/> and the Dapr shell attaches it to a FRESH
    /// message on every retry attempt. That is only sound if <see cref="HttpClient"/> leaves a
    /// buffered content re-sendable after the first send — which this pins against the real
    /// HttpClient stack. If this ever fails, content must be created inside <c>configure</c>.
    /// </summary>
    [Fact]
    public async Task Buffered_Content_Should_Be_Resendable_On_A_Fresh_Request()
    {
        var handler = new RecordingHandler();
        var client = new HttpClient(handler);
        var content = new StringContent("{\"shared\":true}");

        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://remote.test/one") { Content = content });
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "https://remote.test/two") { Content = content });

        handler.Bodies.ShouldBe(["{\"shared\":true}", "{\"shared\":true}"]);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
