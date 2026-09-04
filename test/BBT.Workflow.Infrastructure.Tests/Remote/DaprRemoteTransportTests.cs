using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Dapr.Client;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins <see cref="DaprRemoteTransport{TClient}"/> against the REAL SDK rewrite: the pipeline is the
/// SDK's own <see cref="InvocationHandler"/> over a recording stub, so what these tests observe is
/// exactly what the sidecar would receive — no emulation of the SDK.
/// </summary>
public sealed class DaprRemoteTransportTests
{
    public sealed class Probe;

    private const string Sidecar = "http://127.0.0.1:3500";

    private static readonly DiscoveryEndpoint Endpoint = new(
        EndpointKind.Dapr,
        new Uri("dapr://vnext-customers-app.preprod-vnext-customers/"),
        "vnext-customers-app.preprod-vnext-customers");

    #region Request Construction (real InvocationHandler)

    [Fact]
    public async Task Should_Rewrite_To_Sidecar_Invoke_Endpoint_Preserving_Query_Verbatim()
    {
        var (sut, stub) = CreateSut();

        await sut.SendAsync(Endpoint, HttpMethod.Get, "/api/v1.0/customers/instances?filter=a%20b&sort=x",
            configure: null, CancellationToken.None);

        // AbsoluteUri, not ToString(): ToString() unescapes for display; AbsoluteUri is the wire form.
        // `%20` surviving is the whole point — the pairs-based SDK overload would have re-escaped it.
        stub.Requests.ShouldHaveSingleItem().RequestUri!.AbsoluteUri.ShouldBe(
            $"{Sidecar}/v1.0/invoke/vnext-customers-app.preprod-vnext-customers/method/api/v1.0/customers/instances?filter=a%20b&sort=x");
    }

    /// <summary>
    /// The cross-namespace form <c>appid.namespace</c> is what Dapr's <c>requestAppIDAndNamespace</c>
    /// splits on; it must reach the sidecar path unmodified.
    /// </summary>
    [Fact]
    public async Task Should_Keep_Cross_Namespace_AppId_Verbatim_In_Path()
    {
        var (sut, stub) = CreateSut();

        await sut.SendAsync(Endpoint, HttpMethod.Post, "api/v1.0/x", null, CancellationToken.None);

        stub.Requests[0].RequestUri!.AbsolutePath
            .ShouldBe("/v1.0/invoke/vnext-customers-app.preprod-vnext-customers/method/api/v1.0/x");
    }

    [Fact]
    public async Task Should_Deliver_Configured_Content_And_Headers()
    {
        var (sut, stub) = CreateSut();

        await sut.SendAsync(Endpoint, HttpMethod.Post, "api/v1.0/x", req =>
        {
            req.Content = new StringContent("{\"a\":1}");
            req.Headers.TryAddWithoutValidation("X-Request-Id", "rid-1");
        }, CancellationToken.None);

        stub.Bodies.ShouldBe(["{\"a\":1}"]);
        stub.Requests[0].Headers.GetValues("X-Request-Id").ShouldBe(["rid-1"]);
    }

    [Fact]
    public async Task Should_Throw_HttpRequestException_When_Endpoint_Has_No_AppId()
    {
        var (sut, _) = CreateSut();
        var noAppId = new DiscoveryEndpoint(EndpointKind.Dapr, new Uri("dapr://x/"), DaprAppId: null);

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.SendAsync(noAppId, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));
    }

    #endregion

    #region Failure Normalization

    /// <summary>A socket failure to the sidecar is a native HttpRequestException on this path and must stay one.</summary>
    [Fact]
    public async Task Sidecar_Socket_Failure_Should_Surface_As_HttpRequestException()
    {
        var (sut, _) = CreateSut(respond: _ => throw new HttpRequestException("connection refused"));

        await Should.ThrowAsync<HttpRequestException>(
            () => sut.SendAsync(Endpoint, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));
    }

    /// <summary>
    /// An unreachable callee does NOT fail the socket — the sidecar answers 500 with a Dapr error
    /// body. It must still surface as a transport failure, not a permanent remote 5xx.
    /// </summary>
    [Theory]
    [InlineData("ERR_DIRECT_INVOKE")]
    [InlineData("ERR_SERVICE_DISCOVERY")]
    public async Task Should_Throw_HttpRequestException_On_Sidecar_Error_Body(string errorCode)
    {
        var (sut, _) = CreateSut(respond: _ => Response(
            HttpStatusCode.InternalServerError, $"{{\"errorCode\":\"{errorCode}\",\"message\":\"fail\"}}"));

        var ex = await Should.ThrowAsync<HttpRequestException>(
            () => sut.SendAsync(Endpoint, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None));

        ex.Message.ShouldContain(errorCode);
    }

    /// <summary>Only a vNext app emits <c>_aether_error_format</c>; its presence proves the callee answered.</summary>
    [Fact]
    public async Task Should_Pass_Through_Callee_Error_With_Aether_Header()
    {
        var (sut, _) = CreateSut(respond: _ => Response(
            HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"Dependency:500\"}}", aether: true));

        var response = await sut.SendAsync(Endpoint, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task Should_Pass_Through_4xx_Even_With_Error_Code_Body()
    {
        var (sut, _) = CreateSut(respond: _ => Response(HttpStatusCode.NotFound, "{\"errorCode\":\"ERR_X\"}"));

        var response = await sut.SendAsync(Endpoint, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>MapToErrorAsync reads the body after this transport inspected it — it must remain readable.</summary>
    [Fact]
    public async Task Should_Leave_Error_Body_Readable_After_Inspection()
    {
        var (sut, _) = CreateSut(respond: _ => Response(HttpStatusCode.InternalServerError, "{\"notAnErrorCode\":true}"));

        var response = await sut.SendAsync(Endpoint, HttpMethod.Get, "api/v1.0/x", null, CancellationToken.None);

        (await response.Content.ReadAsStringAsync()).ShouldBe("{\"notAnErrorCode\":true}");
    }

    #endregion

    #region Resilience

    /// <summary>
    /// A sent <see cref="HttpRequestMessage"/> cannot be sent again, so every attempt must build a
    /// fresh one and re-run <c>configure</c> on it.
    /// </summary>
    [Fact]
    public async Task Read_Profile_Should_Build_A_Fresh_Request_Per_Attempt()
    {
        var configureCalls = 0;
        var (sut, stub) = CreateSut(
            profile: RemoteServiceProfile.Read,
            respond: _ => Response(HttpStatusCode.ServiceUnavailable, "{}"));

        await sut.SendAsync(Endpoint, HttpMethod.Post, "api/v1.0/x", _ => configureCalls++, CancellationToken.None);

        // 1 initial + 2 retries
        stub.Requests.Count.ShouldBe(3);
        stub.Requests.Distinct().Count().ShouldBe(3, "each attempt must be a distinct request instance");
        configureCalls.ShouldBe(3);
    }

    /// <summary>The decision that protects against duplicated side effects, on the Dapr wire too.</summary>
    [Fact]
    public async Task Mutating_Profile_Should_Attempt_Exactly_Once()
    {
        var (sut, stub) = CreateSut(
            profile: RemoteServiceProfile.Mutating,
            respond: _ => Response(HttpStatusCode.ServiceUnavailable, "{}"));

        await sut.SendAsync(Endpoint, HttpMethod.Post, "api/v1.0/x", null, CancellationToken.None);

        stub.Requests.Count.ShouldBe(1);
    }

    #endregion

    private static (DaprRemoteTransport<Probe> Sut, RecordingHandler Stub) CreateSut(
        RemoteServiceProfile profile = RemoteServiceProfile.Read,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var stub = new RecordingHandler(respond ?? (_ => Response(HttpStatusCode.OK, "{}")));

        // The SDK's real handler, pointed at a fixed sidecar address so the test does not depend on
        // DAPR_HTTP_ENDPOINT / DAPR_HTTP_PORT in the environment. Production uses
        // DaprClient.CreateInvokeHttpClient(), which builds exactly this pipeline.
        var invokeClient = new HttpClient(new InvocationHandler { InnerHandler = stub, DaprEndpoint = Sidecar });

        var options = new RemoteOptions { TimeoutSeconds = 30, MaxRetryAttempts = 2, RetryDelayMilliseconds = 1 };
        return (new DaprRemoteTransport<Probe>(invokeClient, RemotePolicyFactory.Compose(options, profile)), stub);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body, bool aether = false)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
        if (aether) response.Headers.TryAddWithoutValidation("_aether_error_format", "true");
        return response;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Snapshot the URI: InvocationHandler restores the original in its finally, so a
            // reference read after the call would show http://{appId}/... instead of the wire form.
            var snapshot = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) snapshot.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(snapshot);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request);
        }
    }
}
