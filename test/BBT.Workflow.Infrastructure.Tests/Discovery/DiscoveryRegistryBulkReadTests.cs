using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins <c>DiscoveryRegistryClient.ListAllAsync</c>, the bulk read that fills the discovery cache.
/// </summary>
/// <remarks>
/// The removed bulk cache (<c>79da3b6f</c>) failed here in a way nothing caught: it followed the
/// response's <c>links.next</c>, which the remote generates with its own API-gateway base path, and
/// swallowed the resulting 404 — so it cached the first page and only the first page, forever, with
/// nothing in the logs. <see cref="Pagination_ignores_links_next_and_increments_the_page_number"/>
/// and <see cref="Page_cap_bounds_a_registry_that_ignores_the_page_parameter"/> are the guards
/// against re-introducing that class of silent truncation.
/// </remarks>
public sealed class DiscoveryRegistryBulkReadTests
{
    private const string BaseUrl = "https://discovery.test/api/v1";

    [Fact]
    public async Task Parses_the_registry_list_envelope()
    {
        var sut = CreateSut(out _, _ => Page(RealWorldPayloadItems()));

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();

        var domains = result.Value!.Select(r => r.DomainName).ToList();
        domains.ShouldContain("credit");
        domains.ShouldContain("onboarding");
        domains.ShouldContain("morph-idm");

        var credit = result.Value!.Single(r => r.DomainName == "credit");
        credit.AppId.ShouldBe("vnext-credit-app");
        credit.BaseUrl.ShouldBe("http://vnext-credit-orchestrator.intprod-vnext-credit.svc.cluster.local:5000");
        credit.HealthUrl.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Registration_without_a_domain_name_attribute_falls_back_to_the_instance_key()
    {
        var sut = CreateSut(out _, _ => Page([
            """{"key":"legacy","metadata":{"status":"A"},"attributes":{"baseUrl":"https://legacy.test"}}"""
        ]));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // The registry sets the instance key to the domain name, so it is a sound fallback for a
        // registration written before the attribute existed.
        result.Value!.Single().DomainName.ShouldBe("legacy");
    }

    [Fact]
    public async Task Inactive_registrations_are_excluded_client_side()
    {
        var sut = CreateSut(out _, _ => Page([
            Item("live", "A"),
            Item("retired", "P"),
            Item("finished", "C")
        ]));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // The server-side filter is an optimisation that may have been dropped mid-run, so the
        // client-side check has to be the authoritative one.
        result.Value!.Select(r => r.DomainName).ShouldBe(["live"]);
    }

    [Fact]
    public async Task Accepted_statuses_are_configurable()
    {
        var sut = CreateSut(
            out _,
            _ => Page([Item("live", "A"), Item("finished", "C")]),
            configureCache: c => c.AcceptedStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A", "C" });

        var result = await sut.ListAllAsync(CancellationToken.None);

        // metadata.status is the registration WORKFLOW's status, not a health signal. A deployment
        // whose registration flow runs to a Finish state leaves every instance at C, and hard-coding
        // "A" would skip every one of its domains.
        result.Value!.Select(r => r.DomainName).OrderBy(d => d).ShouldBe(["finished", "live"]);
    }

    [Fact]
    public async Task Pagination_ignores_links_next_and_increments_the_page_number()
    {
        var sut = CreateSut(out var handler, request =>
        {
            var page = PageNumber(request);

            // The shape the real registry returns: a gateway-rooted path that does NOT carry the
            // configured /api/v1 prefix. Following it is how the previous implementation broke.
            return page == 1
                ? Page(Enumerable.Range(0, 100).Select(i => Item($"d{i}", "A")),
                       next: "/ebanking/discovery/workflows/domain/instances?page=2&pageSize=100")
                : Page([Item("last", "A")]);
        }, configureCache: c => c.BulkPageSize = 100);

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(101);

        var urls = handler.Requests.Select(r => r.RequestUri!.ToString()).ToList();
        urls.Count.ShouldBe(2);
        urls.ShouldAllBe(u => u.StartsWith(BaseUrl, StringComparison.Ordinal));
        urls.ShouldAllBe(u => !u.Contains("/ebanking/", StringComparison.Ordinal));
        urls[0].ShouldContain("page=1");
        urls[1].ShouldContain("page=2");
    }

    [Fact]
    public async Task A_short_page_ends_pagination()
    {
        var sut = CreateSut(out var handler, _ => Page([Item("only", "A")]),
            configureCache: c => c.BulkPageSize = 100);

        await sut.ListAllAsync(CancellationToken.None);

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_empty_page_ends_pagination()
    {
        var sut = CreateSut(out var handler, request =>
            PageNumber(request) == 1
                ? Page(Enumerable.Range(0, 2).Select(i => Item($"d{i}", "A")))
                : Page([]),
            configureCache: c => c.BulkPageSize = 2);

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.Value!.Count.ShouldBe(2);
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Pagination_stops_when_the_registry_repeats_a_page()
    {
        var sut = CreateSut(out var handler,
            _ => Page(Enumerable.Range(0, 2).Select(i => Item($"d{i}", "A"))),
            configureCache: c =>
            {
                c.BulkPageSize = 2;
                c.MaxPages = 20;
            });

        var result = await sut.ListAllAsync(CancellationToken.None);

        // A registry that ignores `page` would otherwise loop to MaxPages, re-adding the same domains
        // each time and burning the whole refresh lease to achieve nothing.
        handler.Requests.Count.ShouldBe(2);
        result.Value!.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Page_cap_bounds_a_registry_that_ignores_the_page_parameter()
    {
        var counter = 0;
        var sut = CreateSut(out var handler,
            _ => Page(Enumerable.Range(0, 2).Select(_ => Item($"d{counter++}", "A"))),
            configureCache: c =>
            {
                c.BulkPageSize = 2;
                c.MaxPages = 3;
            });

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        handler.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task A_failed_page_fails_the_whole_read_rather_than_returning_a_partial_list()
    {
        var sut = CreateSut(out _, request =>
                PageNumber(request) == 1
                    ? Page(Enumerable.Range(0, 2).Select(i => Item($"d{i}", "A")))
                    : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("boom")
                    },
            configureCache: c => c.BulkPageSize = 2);

        var result = await sut.ListAllAsync(CancellationToken.None);

        // A partial list is indistinguishable from a registry that genuinely lost domains, and
        // publishing one would evict good entries in favour of nothing.
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task A_rejected_status_filter_is_retried_unfiltered()
    {
        var sut = CreateSut(out var handler, request =>
            request.RequestUri!.Query.Contains("filter=", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("bad filter") }
                : Page([Item("live", "A")]));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // Older runtimes 400 on this filter shape because their legacy path parses the status through
        // Enum.Parse and InstanceStatus is a sealed class, not an enum. The filter is only ever an
        // optimisation, so a rejection must not fail the refresh.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Single().DomainName.ShouldBe("live");
        handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Missing_base_url_fails_without_any_http_call()
    {
        var sut = CreateSut(out var handler, _ => Page([]), baseUrl: string.Empty);

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    // ────────────────────────────────────────────────────────────────────
    // Harness
    // ────────────────────────────────────────────────────────────────────

    private static DiscoveryRegistryClient CreateSut(
        out DomainDiscoveryResolverTests.RoutingHandler handler,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<DiscoveryCacheOptions>? configureCache = null,
        string? baseUrl = null)
    {
        handler = new DomainDiscoveryResolverTests.RoutingHandler(respond);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(handler));

        var cache = new DiscoveryCacheOptions { Enabled = true };
        configureCache?.Invoke(cache);

        var options = Options.Create(new ServiceDiscoveryOptions
        {
            Enabled = true,
            BaseUrl = baseUrl ?? BaseUrl,
            Domain = "discovery",
            Cache = cache
        });

        return new DiscoveryRegistryClient(httpClientFactory, options, NullLogger<DiscoveryRegistryClient>.Instance);
    }

    private static int PageNumber(HttpRequestMessage request)
    {
        var query = request.RequestUri!.Query;
        var marker = query.IndexOf("page=", StringComparison.Ordinal);
        if (marker < 0)
            return 1;

        var digits = new string(query[(marker + 5)..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var page) ? page : 1;
    }

    private static string Item(string domain, string status) =>
        "{\"key\":\"" + domain + "\",\"metadata\":{\"status\":\"" + status + "\"},\"attributes\":{" +
        "\"domainName\":\"" + domain + "\",\"baseUrl\":\"https://" + domain + ".test\"," +
        "\"appId\":\"vnext-" + domain + "-app\",\"healthUrl\":\"https://" + domain + ".test/health\"}}";

    private static HttpResponseMessage Page(IEnumerable<string> items, string next = "")
    {
        var body = $$"""
                     {"links":{"self":"/ebanking/discovery/workflows/domain/instances?page=1&pageSize=100","next":"{{next}}"},"items":[{{string.Join(",", items)}}]}
                     """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// A trimmed copy of a real registry response, kept verbatim in shape so a change to the
    /// envelope is caught here rather than in production.
    /// </summary>
    private static IEnumerable<string> RealWorldPayloadItems() =>
    [
        """{"id":"060b70cc","key":"credit","flow":"domain","domain":"discovery","metadata":{"currentState":"domain-registered","status":"A"},"attributes":{"appId":"vnext-credit-app","baseUrl":"http://vnext-credit-orchestrator.intprod-vnext-credit.svc.cluster.local:5000","healthUrl":"http://vnext-credit-orchestrator.intprod-vnext-credit.svc.cluster.local:5000/health","domainName":"credit","cacheInvalidated":true},"extensions":{}}""",
        """{"id":"06a7c134","key":"onboarding","flow":"domain","domain":"discovery","metadata":{"currentState":"domain-registered","status":"A"},"attributes":{"appId":"vnext-onboarding-app","baseUrl":"http://vnext-onboarding-orchestrator.intprod-vnext-onboarding.svc.cluster.local:5000","healthUrl":"http://vnext-onboarding-orchestrator.intprod-vnext-onboarding.svc.cluster.local:5000/health","domainName":"onboarding"},"extensions":{}}""",
        """{"id":"101de37f","key":"morph-idm","flow":"domain","domain":"discovery","metadata":{"currentState":"domain-registered","status":"A"},"attributes":{"appId":"vnext-morph-idm-app","baseUrl":"http://vnext-morph-idm-orchestrator.intprod-vnext-morph-idm.svc.cluster.local:5000","healthUrl":"http://vnext-morph-idm-orchestrator.intprod-vnext-morph-idm.svc.cluster.local:5000/health","domainName":"morph-idm"},"extensions":{}}"""
    ];
}
