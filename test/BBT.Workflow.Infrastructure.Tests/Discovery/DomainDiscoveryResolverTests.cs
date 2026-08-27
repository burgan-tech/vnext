using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow;
using BBT.Workflow.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins the post-cache-removal contract of <see cref="DomainDiscoveryResolver"/>: every resolution
/// queries the discovery registry directly over HTTP, with no bulk cache, no ETag revalidation, and
/// no <c>IDistributedCacheService</c>/<c>IDistributedLockService</c> dependency in the constructor.
/// Follows the inline stub-<see cref="HttpMessageHandler"/> pattern established by
/// <c>RemoteRelatedInstanceReaderTests.RoutingHandler</c>; there is no shared mocking library for
/// <see cref="HttpClient"/> in this codebase.
/// </summary>
public sealed class DomainDiscoveryResolverTests
{
    private const string Domain = "lending";

    private static (DomainDiscoveryResolver Resolver, RoutingHandler Handler) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        ServiceDiscoveryOptions? options = null)
    {
        var handler = new RoutingHandler(respond ?? (_ => SuccessResponse()));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var resolvedOptions = options ?? new ServiceDiscoveryOptions
        {
            Enabled = true,
            BaseUrl = "https://discovery.test",
            Domain = "core"
        };

        var resolver = new DomainDiscoveryResolver(
            httpClientFactory,
            Options.Create(resolvedOptions),
            NullLogger<DomainDiscoveryResolver>.Instance);

        return (resolver, handler);
    }

    private static HttpResponseMessage SuccessResponse(string domain = Domain, string? appId = "lending-app")
    {
        var body = $$"""
            {
              "data": {
                "domainName": "{{domain}}",
                "baseUrl": "https://{{domain}}.internal.test",
                "appId": {{(appId is null ? "null" : $"\"{appId}\"")}}
              },
              "eTag": "some-etag"
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    [Fact]
    public async Task Every_resolution_queries_discovery_no_caching()
    {
        var (resolver, handler) = CreateSut();

        var first = await resolver.GetEndpointAsync(Domain, EndpointKind.Url, CancellationToken.None);
        var second = await resolver.GetEndpointAsync(Domain, EndpointKind.Url, CancellationToken.None);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();

        // The test that would fail if anyone reintroduces a cache: a repeated resolution for the
        // SAME domain must still hit the wire a second time, because there is nothing to serve it
        // from otherwise.
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetEndpointAsync_WhenDiscoveryDisabled_FailsWithoutAnyHttpCall()
    {
        var (resolver, handler) = CreateSut(options: new ServiceDiscoveryOptions
        {
            Enabled = false,
            BaseUrl = "https://discovery.test",
            Domain = "core"
        });

        var result = await resolver.GetEndpointAsync(Domain, EndpointKind.Url, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetEndpointAsync_NotFound_MapsToDomainEndpointNotFound()
    {
        var (resolver, _) = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found")
        });

        var result = await resolver.GetEndpointAsync(Domain, EndpointKind.Url, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.DomainEndpointNotFound);
    }

    [Fact]
    public async Task GetEndpointAsync_Success_ReturnsEndpointWithUriAndAppId()
    {
        var (resolver, _) = CreateSut(_ => SuccessResponse(Domain, "lending-app"));

        var result = await resolver.GetEndpointAsync(Domain, EndpointKind.Dapr, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.BaseUrl.ShouldBe(new Uri("https://lending.internal.test/"));
        result.Value!.DaprAppId.ShouldBe("lending-app");
        // preferredKind honoured through DetermineEndpointKind: AppId is present, so Dapr stays Dapr.
        result.Value!.Kind.ShouldBe(EndpointKind.Dapr);
    }

    [Fact]
    public async Task GetEndpointAsync_PreferredDaprWithoutAppId_FallsBackToUrl()
    {
        var (resolver, _) = CreateSut(_ => SuccessResponse(Domain, appId: null));

        var result = await resolver.GetEndpointAsync(Domain, EndpointKind.Dapr, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Kind.ShouldBe(EndpointKind.Url);
        result.Value!.DaprAppId.ShouldBeNull();
    }

    [Fact]
    public async Task GetEndpointAsync_NeverSendsIfNoneMatchHeader()
    {
        var (resolver, handler) = CreateSut();

        await resolver.GetEndpointAsync(Domain, EndpointKind.Url, CancellationToken.None);
        await resolver.GetEndpointAsync(Domain, EndpointKind.Url, CancellationToken.None);

        handler.Requests.ShouldAllBe(r => !r.Headers.Contains("If-None-Match"));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
