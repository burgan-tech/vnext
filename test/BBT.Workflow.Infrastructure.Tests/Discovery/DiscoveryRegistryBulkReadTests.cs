using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins <c>DiscoveryRegistryClient.ListAllAsync</c>, the bulk read that fills the discovery cache.
/// </summary>
/// <remarks>
/// The read is one call to the registry's Domain-scope <c>domain-list</c> function, which owns the
/// whole projection: it filters to active registrations, orders them, resolves the domain name from
/// the instance key and drops anything unroutable. The client re-derives none of that — the tests
/// below exist to keep it that way, and to pin the two things the runtime is still responsible for:
/// refusing to publish a partial list as a success, and saying so loudly when the single page the
/// function serves comes back full.
/// </remarks>
public sealed class DiscoveryRegistryBulkReadTests
{
    private const string BaseUrl = "https://discovery.test/api/v1";

    [Fact]
    public async Task Parses_the_domain_list_envelope()
    {
        var sut = CreateSut(out _, out _, _ => DomainList(RealWorldPayloadItems()));

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
    public async Task Reads_the_whole_registry_in_one_call_with_no_pagination()
    {
        var sut = CreateSut(out var handler, out _,
            _ => DomainList(Enumerable.Range(0, 100).Select(i => Item($"d{i}"))));

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.Value!.Count.ShouldBe(100);

        // A full response is not a page. The function is Domain-scope and answers for the whole
        // registry, so asking for a second one would re-read the same list, not continue it.
        handler.Requests.Count.ShouldBe(1);

        var url = handler.Requests.Single().RequestUri!.ToString();
        url.ShouldBe($"{BaseUrl}/discovery/functions/domain-list");
        url.ShouldNotContain("page=");
        url.ShouldNotContain("filter=");
    }

    [Fact]
    public async Task Endpoint_template_is_configurable()
    {
        var sut = CreateSut(out var handler, out _, _ => DomainList([Item("live")]),
            configureCache: c => c.DomainListEndpointTemplate = "/gw/{0}/functions/domain-list");

        await sut.ListAllAsync(CancellationToken.None);

        // An API gateway can sit in front of the registry with a different path; that must stay a
        // config change rather than a code change.
        handler.Requests.Single().RequestUri!.ToString()
            .ShouldBe($"{BaseUrl}/gw/discovery/functions/domain-list");
    }

    [Fact]
    public async Task An_empty_registry_is_a_success_not_a_failure()
    {
        var sut = CreateSut(out _, out _, _ => DomainList([]));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // The function answers 200 with an empty array for an empty registry, and this client passes
        // it through as an empty success. Deciding that an empty registry is not worth publishing is
        // DiscoveryCacheRefresher's call, not the reader's.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_listed_domain_without_a_name_is_skipped()
    {
        var sut = CreateSut(out _, out _, _ => DomainList([
            """{"domainName":"","baseUrl":"https://nameless.test"}""",
            Item("live")
        ]));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // Nameless is unreachable by definition: the name IS the cache key every lookup resolves by.
        result.Value!.Select(r => r.DomainName).ShouldBe(["live"]);
    }

    [Fact]
    public async Task Blank_base_url_and_app_id_are_normalised_to_null()
    {
        var sut = CreateSut(out _, out _, _ => DomainList([
            """{"domainName":"sparse","baseUrl":"","appId":"","healthUrl":""}"""
        ]));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // Requiring a baseUrl is the HTTP provider's rule and is enforced there; a registration
        // carrying only a name and an app-id is entirely valid under Dapr.
        var sparse = result.Value!.Single();
        sparse.BaseUrl.ShouldBeNull();
        sparse.AppId.ShouldBeNull();
    }

    [Fact]
    public async Task A_full_page_is_published_but_warns_that_the_list_may_be_truncated()
    {
        var sut = CreateSut(out _, out var logger,
            _ => DomainList(Enumerable.Range(0, 10).Select(i => Item($"d{i}"))),
            configureCache: c => c.DomainListExpectedMax = 10);

        var result = await sut.ListAllAsync(CancellationToken.None);

        // The response carries no truncation signal, so a full page is the only evidence available —
        // and the list is still published, because the domains it does hold are served from cache
        // while the ones it misses merely pay a live lookup. Refusing to publish would make every
        // domain pay it.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Count.ShouldBe(10);
        logger.Warnings.ShouldContain(w => w.Contains("may be truncated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_list_below_the_expected_maximum_does_not_warn()
    {
        var sut = CreateSut(out _, out var logger,
            _ => DomainList(Enumerable.Range(0, 9).Select(i => Item($"d{i}"))),
            configureCache: c => c.DomainListExpectedMax = 10);

        await sut.ListAllAsync(CancellationToken.None);

        logger.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_read_fails_rather_than_returning_an_empty_list()
    {
        var sut = CreateSut(out _, out _, _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });

        var result = await sut.ListAllAsync(CancellationToken.None);

        // An empty list and a failed read must not look alike: publishing the former would evict
        // every good entry in favour of nothing.
        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task A_registry_without_the_domain_list_function_fails_loudly()
    {
        var sut = CreateSut(out _, out var logger, _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.ListAllAsync(CancellationToken.None);

        // The one failure with a configuration cause: a discovery deployment whose package predates
        // domain-list. There is no fallback read any more, so this log line is the only guidance —
        // the cache stays cold and every lookup resolves live, which is the cache-off behaviour.
        result.IsSuccess.ShouldBeFalse();
        logger.Warnings.ShouldContain(w => w.Contains("404", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_base_url_fails_without_any_http_call()
    {
        var sut = CreateSut(out var handler, out _, _ => DomainList([]), baseUrl: string.Empty);

        var result = await sut.ListAllAsync(CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        handler.Requests.ShouldBeEmpty();
    }

    // ────────────────────────────────────────────────────────────────────
    // Harness
    // ────────────────────────────────────────────────────────────────────

    private static DiscoveryRegistryClient CreateSut(
        out DomainDiscoveryResolverTests.RoutingHandler handler,
        out CapturingLogger logger,
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<DiscoveryCacheOptions>? configureCache = null,
        string? baseUrl = null)
    {
        handler = new DomainDiscoveryResolverTests.RoutingHandler(respond);
        logger = new CapturingLogger();

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

        return new DiscoveryRegistryClient(httpClientFactory, options, logger);
    }

    private static string Item(string domain) =>
        "{\"domainName\":\"" + domain + "\",\"baseUrl\":\"https://" + domain + ".test\"," +
        "\"appId\":\"vnext-" + domain + "-app\",\"healthUrl\":\"https://" + domain + ".test/health\"}";

    private static HttpResponseMessage DomainList(IEnumerable<string> items)
    {
        var body = $$"""{"items":[{{string.Join(",", items)}}]}""";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// A trimmed copy of a real <c>domain-list</c> response, kept verbatim in shape so a change to
    /// the envelope is caught here rather than in production.
    /// </summary>
    private static IEnumerable<string> RealWorldPayloadItems() =>
    [
        """{"domainName":"credit","baseUrl":"http://vnext-credit-orchestrator.intprod-vnext-credit.svc.cluster.local:5000","appId":"vnext-credit-app","healthUrl":"http://vnext-credit-orchestrator.intprod-vnext-credit.svc.cluster.local:5000/health"}""",
        """{"domainName":"onboarding","baseUrl":"http://vnext-onboarding-orchestrator.intprod-vnext-onboarding.svc.cluster.local:5000","appId":"vnext-onboarding-app","healthUrl":"http://vnext-onboarding-orchestrator.intprod-vnext-onboarding.svc.cluster.local:5000/health"}""",
        """{"domainName":"morph-idm","baseUrl":"http://vnext-morph-idm-orchestrator.intprod-vnext-morph-idm.svc.cluster.local:5000","appId":"vnext-morph-idm-app","healthUrl":"http://vnext-morph-idm-orchestrator.intprod-vnext-morph-idm.svc.cluster.local:5000/health"}"""
    ];

    /// <summary>
    /// Collects Warning-level messages. The truncation and missing-endpoint cases are observable
    /// ONLY as log lines — both return an otherwise ordinary result — so a null logger would leave
    /// them untested.
    /// </summary>
    internal sealed class CapturingLogger : ILogger<DiscoveryRegistryClient>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }
}
