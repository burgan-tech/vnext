using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow;
using BBT.Workflow.Discovery;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
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
/// <para>
/// Also pins the <c>Discovery.Resolve/{domain}</c> span (see <see cref="DomainDiscoveryResolverSpanTests"/>
/// below): the resolver is called from 32 sites, and a span inside <c>GetEndpointAsync</c> parents to
/// whatever is ambient so it shows up under every caller with no per-call-site changes.
/// </para>
/// </summary>
public sealed class DomainDiscoveryResolverTests
{
    private const string Domain = "lending";

    internal static (DomainDiscoveryResolver Resolver, RoutingHandler Handler) CreateSut(
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

    internal static HttpResponseMessage SuccessResponse(string domain = Domain, string? appId = "lending-app")
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

    internal sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
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

/// <summary>
/// Pins the <c>Discovery.Resolve/{domain}</c> span emitted by <see cref="DomainDiscoveryResolver.GetEndpointAsync"/>.
/// <para>
/// Before this span existed, cross-domain endpoint resolution was invisible in the trace: no span of
/// its own, and the discovery HTTP call showed up as an unattributed HttpClient span wherever it
/// happened to be ambient. The resolver is called from 32 sites, so a span inside
/// <c>GetEndpointAsync</c> (rather than at each call site) makes every one of them attributable with
/// no per-call-site edits.
/// </para>
/// <para>
/// Reuses <see cref="PipelineStepActivityHelper.ActivitySource"/> (<c>BBT.Workflow.Pipeline</c>) —
/// already registered in every host's <c>Telemetry:Tracing:AdditionalSources</c> — rather than a new
/// <see cref="ActivitySource"/>, so the span cannot be silently invisible for lack of registration.
/// </para>
/// </summary>
public sealed class DomainDiscoveryResolverSpanTests : IDisposable
{
    private readonly List<Activity> _collected = new();
    private readonly ActivityListener _listener;

    public DomainDiscoveryResolverSpanTests()
    {
        _listener = new ActivityListener
        {
            // Hardcoded name literal rather than dereferencing PipelineStepActivityHelper.ActivitySource.Name:
            // the first access to that static field in a test process runs the class's static
            // constructor, which itself calls `new ActivitySource(...)` and notifies already-registered
            // listeners synchronously — including this one, on the very field being assigned. Referencing
            // the field back from inside that callback observes it before assignment completes and throws.
            ShouldListenTo = s => s.Name == "BBT.Workflow.Pipeline",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _collected.Add
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    // Each test uses a domain name unique to itself, never "lending" (the constant used by the
    // sibling DomainDiscoveryResolverTests class): xUnit runs different test classes in parallel by
    // default, and the ActivityListener here is scoped to the ActivitySource, not to this test's own
    // resolver instance — a same-named "Discovery.Resolve/lending" span from a concurrently running
    // DomainDiscoveryResolverTests test would land in this class's _collected list too.

    [Fact]
    public async Task Successful_resolution_emits_exactly_one_span_named_after_the_domain()
    {
        const string domain = "span-emit-probe";
        var (resolver, _) = DomainDiscoveryResolverTests.CreateSut(
            _ => DomainDiscoveryResolverTests.SuccessResponse(domain, "span-emit-probe-app"));

        var result = await resolver.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        var spans = _collected.Where(a => a.DisplayName == $"Discovery.Resolve/{domain}").ToList();
        spans.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Successful_resolution_tags_domain_and_endpoint_kind()
    {
        const string domain = "span-tags-probe";
        var (resolver, _) = DomainDiscoveryResolverTests.CreateSut(
            _ => DomainDiscoveryResolverTests.SuccessResponse(domain, "span-tags-probe-app"));

        await resolver.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);

        var span = _collected.Single(a => a.DisplayName == $"Discovery.Resolve/{domain}");
        span.GetTagItem(TelemetryConstants.TagNames.DiscoveryDomain).ShouldBe(domain);
        span.GetTagItem(TelemetryConstants.TagNames.DiscoveryEndpointKind).ShouldBe(EndpointKind.Url.ToString());
        (span.Status is ActivityStatusCode.Unset or ActivityStatusCode.Ok).ShouldBeTrue();
    }

    [Fact]
    public async Task Failed_resolution_NotFound_producesAnErrorSpan()
    {
        const string domain = "span-notfound-probe";
        var (resolver, _) = DomainDiscoveryResolverTests.CreateSut(
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") });

        var result = await resolver.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();

        var span = _collected.Single(a => a.DisplayName == $"Discovery.Resolve/{domain}");
        span.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Span_parents_to_the_ambient_activity()
    {
        const string domain = "span-parent-probe";
        var ambient = new Activity("Transition.Validate");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();

        try
        {
            var (resolver, _) = DomainDiscoveryResolverTests.CreateSut(
                _ => DomainDiscoveryResolverTests.SuccessResponse(domain, "span-parent-probe-app"));

            await resolver.GetEndpointAsync(domain, EndpointKind.Url, CancellationToken.None);
        }
        finally
        {
            ambient.Stop();
            Activity.Current = null;
        }

        var span = _collected.Single(a => a.DisplayName == $"Discovery.Resolve/{domain}");
        span.ParentId.ShouldBe(ambient.Id);
    }
}
