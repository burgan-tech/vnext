using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BBT.Workflow.Discovery;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins <see cref="DaprDomainDiscoveryProvider"/>: convention-derived app-ids, the
/// cross-namespace qualification Dapr requires, the registry override, and the caching rules.
/// </summary>
public sealed class DaprDomainDiscoveryProviderTests
{
    private const string Domain = "customers";

    #region Convention Tests

    /// <summary>
    /// Pins the runtime DEFAULT: <c>new DaprDiscoveryOptions()</c> never reads the registry. The
    /// whole point of moving resolution to Dapr was to stop asking the discovery domain on every
    /// cross-domain call, so <c>RequireRegistryEntry</c> is opt-in (decision 2026-09-04). A
    /// registry-supplied app-id is therefore invisible by default — <c>DomainOverrides</c> or the
    /// opt-in flag are the two ways to a non-conventional app-id.
    /// </summary>
    [Fact]
    public async Task Default_Options_Should_Never_Read_The_Registry()
    {
        var defaults = new DaprDiscoveryOptions();
        defaults.RequireRegistryEntry.ShouldBeFalse();

        var (sut, handler) = CreateSut(
            respond: _ => RegistryResponse(appId: "legacy-customers-app"),
            dapr: defaults);

        var result = await sut.GetEndpointAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.DaprAppId.ShouldBe("vnext-customers-app");
        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The headline win: the common path resolves with NO registry round trip at all, where the
    /// http provider queries the registry on every single cross-domain call.
    /// </summary>
    [Fact]
    public async Task Should_Resolve_By_Convention_Without_Touching_The_Registry()
    {
        var (sut, handler) = CreateSut(dapr: new DaprDiscoveryOptions { RequireRegistryEntry = false });

        var result = await sut.GetEndpointAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Kind.ShouldBe(EndpointKind.Dapr);
        result.Value!.DaprAppId.ShouldBe("vnext-customers-app");
        result.Value!.BaseUrl.ToString().ShouldBe("dapr://vnext-customers-app/");
        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// The endpoint must end in a slash: callers compose with
    /// <c>new Uri(BaseUrl, relativePath)</c>, and without it the last segment is replaced
    /// rather than appended.
    /// </summary>
    [Fact]
    public async Task Endpoint_Should_Compose_With_Relative_Paths()
    {
        var (sut, _) = CreateSut(dapr: new DaprDiscoveryOptions { RequireRegistryEntry = false });

        var result = await sut.GetEndpointAsync(Domain);
        var composed = new Uri(result.Value!.BaseUrl, "api/v1.0/customers/instances");

        composed.ToString().ShouldBe("dapr://vnext-customers-app/api/v1.0/customers/instances");
    }

    #endregion

    #region Cross-Namespace Tests

    /// <summary>
    /// Required, not cosmetic: Dapr's <c>requestAppIDAndNamespace</c> defaults the namespace to
    /// the CALLER's own when the app-id carries no dot, so a bare app-id would be resolved in the
    /// wrong namespace. The same pair also builds the expected SPIFFE identity for mTLS.
    /// </summary>
    [Fact]
    public async Task Should_Qualify_AppId_With_Target_Namespace()
    {
        var (sut, _) = CreateSut(dapr: new DaprDiscoveryOptions
        {
            RequireRegistryEntry = false,
            NamespaceTemplate = "preprod-vnext-{domain}"
        });

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.DaprAppId.ShouldBe("vnext-customers-app.preprod-vnext-customers");
        result.Value!.BaseUrl.Host.ShouldBe("vnext-customers-app.preprod-vnext-customers");
    }

    /// <summary>
    /// Empty template = single-namespace deployment (local docker-compose), where a bare app-id
    /// resolving in the caller's own namespace is exactly right.
    /// </summary>
    [Fact]
    public async Task Should_Leave_AppId_Bare_When_No_Namespace_Template()
    {
        var (sut, _) = CreateSut(dapr: new DaprDiscoveryOptions { RequireRegistryEntry = false });

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.DaprAppId.ShouldBe("vnext-customers-app");
    }

    /// <summary>
    /// Dapr rejects an app-id with more than one dot (<c>invalid app id</c>), so a namespace is
    /// never appended to an app-id that already carries one.
    /// </summary>
    [Fact]
    public async Task Should_Not_Double_Qualify_An_Already_Namespaced_AppId()
    {
        var (sut, _) = CreateSut(dapr: new DaprDiscoveryOptions
        {
            RequireRegistryEntry = false,
            NamespaceTemplate = "preprod-vnext-{domain}",
            DomainOverrides = new(StringComparer.OrdinalIgnoreCase)
            {
                [Domain] = "legacy-app.other-namespace"
            }
        });

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.DaprAppId.ShouldBe("legacy-app.other-namespace");
    }

    #endregion

    #region Registry Override Tests

    [Fact]
    public async Task Registry_AppId_Should_Override_The_Convention()
    {
        var (sut, _) = CreateSut(respond: _ => RegistryResponse(appId: "legacy-customers-app"));

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.DaprAppId.ShouldBe("legacy-customers-app");
    }

    [Fact]
    public async Task Convention_Should_Win_When_PreferRegistryAppId_Is_False()
    {
        var (sut, _) = CreateSut(
            respond: _ => RegistryResponse(appId: "legacy-customers-app"),
            dapr: new DaprDiscoveryOptions { RequireRegistryEntry = true, PreferRegistryAppId = false });

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.DaprAppId.ShouldBe("vnext-customers-app");
    }

    /// <summary>
    /// A registration carrying only <c>domainName</c> + <c>appId</c> is valid under Dapr — the
    /// registry no longer supplies the address. The http provider still requires a baseUrl, which
    /// its own tests pin.
    /// </summary>
    [Fact]
    public async Task Should_Accept_Registration_Without_BaseUrl()
    {
        var (sut, _) = CreateSut(respond: _ => RegistryResponse(baseUrl: null));

        var result = await sut.GetEndpointAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.DaprAppId.ShouldBe("vnext-customers-app");
    }

    #endregion

    #region Registry Requirement Tests

    /// <summary>
    /// With <c>RequireRegistryEntry</c> switched on (opt-in; the runtime default is off), the
    /// registry stays authoritative about which domains exist — preserving the old 404 contract.
    /// </summary>
    [Fact]
    public async Task Unregistered_Domain_Should_Fail_When_Registry_Is_Required()
    {
        var (sut, _) = CreateSut(respond: _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.GetEndpointAsync(Domain);

        result.IsSuccess.ShouldBeFalse();
    }

    /// <summary>
    /// The escape hatch: with the requirement off the registry is never read, so a registry
    /// outage cannot stop cross-domain traffic — the app-id never needed it.
    /// <para>
    /// Also pins that <c>RequireRegistryEntry=false</c> genuinely means "never read the
    /// registry", even with <c>PreferRegistryAppId</c> left at its default true: the two cannot
    /// both hold, and "skip the registry" wins. Getting this wrong reintroduces a network call
    /// on the path whose entire point is not having one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Registry_Should_Not_Be_Read_At_All_When_Not_Required()
    {
        var (sut, handler) = CreateSut(
            respond: _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            dapr: new DaprDiscoveryOptions { RequireRegistryEntry = false, PreferRegistryAppId = true });

        var result = await sut.GetEndpointAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.DaprAppId.ShouldBe("vnext-customers-app");
        handler.Calls.ShouldBe(0);
    }

    /// <summary>
    /// <c>ServiceDiscovery:Enabled</c> governs REGISTRATION. Since the Dapr provider derives the
    /// app-id from the convention, disabling registration must not disable resolution — the http
    /// provider legitimately fails in that case, and conflating the two would make the switch
    /// look broken.
    /// </summary>
    [Fact]
    public async Task Disabled_Discovery_Should_Not_Block_Convention_Resolution()
    {
        var (sut, _) = CreateSut(
            options: new ServiceDiscoveryOptions { Enabled = false, BaseUrl = "https://discovery.test" },
            dapr: new DaprDiscoveryOptions { RequireRegistryEntry = false });

        var result = await sut.GetEndpointAsync(Domain);

        result.IsSuccess.ShouldBeTrue();
    }

    #endregion

    #region Cache Tests

    [Fact]
    public async Task Should_Serve_Repeat_Resolutions_From_Cache()
    {
        var (sut, handler) = CreateSut(dapr: new DaprDiscoveryOptions { RequireRegistryEntry = true, CacheSeconds = 60 });

        await sut.GetEndpointAsync(Domain);
        await sut.GetEndpointAsync(Domain);
        await sut.GetEndpointAsync(Domain);

        handler.Calls.ShouldBe(1);
    }

    /// <summary>
    /// Failures are never cached: a domain that registers a moment later must be picked up
    /// immediately rather than waiting out a TTL.
    /// </summary>
    [Fact]
    public async Task Should_Not_Cache_Failures()
    {
        var responses = new Queue<HttpResponseMessage>(
        [
            new HttpResponseMessage(HttpStatusCode.NotFound),
            RegistryResponse()
        ]);
        var (sut, _) = CreateSut(respond: _ => responses.Dequeue());

        (await sut.GetEndpointAsync(Domain)).IsSuccess.ShouldBeFalse();
        (await sut.GetEndpointAsync(Domain)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_Not_Cache_When_Disabled()
    {
        var (sut, handler) = CreateSut(dapr: new DaprDiscoveryOptions { RequireRegistryEntry = true, CacheSeconds = 0 });

        await sut.GetEndpointAsync(Domain);
        await sut.GetEndpointAsync(Domain);

        handler.Calls.ShouldBe(2);
    }

    #endregion

    #region Per-Domain Override Tests

    /// <summary>
    /// The rollout dial: one domain can be pinned back to plain HTTP without changing the global
    /// provider, which is what makes a staged migration (and a staged rollback) possible.
    /// </summary>
    [Fact]
    public async Task Url_Override_Should_Pin_A_Single_Domain_To_Http()
    {
        var (sut, _) = CreateSut(dapr: new DaprDiscoveryOptions
        {
            DomainOverrides = new(StringComparer.OrdinalIgnoreCase) { [Domain] = "url" }
        });

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.Kind.ShouldBe(EndpointKind.Url);
        result.Value!.BaseUrl.ToString().ShouldBe("https://customers.internal.test/");
    }

    [Fact]
    public async Task Explicit_AppId_Override_Should_Bypass_The_Registry()
    {
        var (sut, handler) = CreateSut(dapr: new DaprDiscoveryOptions
        {
            DomainOverrides = new(StringComparer.OrdinalIgnoreCase) { [Domain] = "bespoke-app" }
        });

        var result = await sut.GetEndpointAsync(Domain);

        result.Value!.DaprAppId.ShouldBe("bespoke-app");
        handler.Calls.ShouldBe(0);
    }

    #endregion

    private static (IDomainDiscoveryResolver Resolver, CountingHandler Handler) CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        ServiceDiscoveryOptions? options = null,
        DaprDiscoveryOptions? dapr = null)
    {
        var handler = new CountingHandler(respond ?? (_ => RegistryResponse()));
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));

        var resolved = options ?? new ServiceDiscoveryOptions
        {
            Enabled = true,
            BaseUrl = "https://discovery.test",
            Domain = "core"
        };
        resolved.Provider = DiscoveryProviders.Dapr;
        // Registry-reading mode by default in THIS harness: most tests here exercise the registry
        // branch (override, 404 contract, cache), which the runtime default (RequireRegistryEntry =
        // false) never enters. The runtime default itself is pinned by
        // Default_Options_Should_Never_Read_The_Registry.
        resolved.Dapr = dapr ?? new DaprDiscoveryOptions { RequireRegistryEntry = true };

        var sut = new DaprDomainDiscoveryProvider(
            new DiscoveryRegistryClient(
                httpClientFactory,
                Options.Create(resolved),
                NullLogger<DiscoveryRegistryClient>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(resolved),
            NullLogger<DaprDomainDiscoveryProvider>.Instance);

        return (sut, handler);
    }

    private static HttpResponseMessage RegistryResponse(
        string? baseUrl = "https://customers.internal.test",
        string? appId = null)
    {
        var body = $$"""
            {
              "data": {
                "domainName": "{{Domain}}",
                "baseUrl": {{(baseUrl is null ? "null" : $"\"{baseUrl}\"")}},
                "appId": {{(appId is null ? "null" : $"\"{appId}\"")}}
              },
              "eTag": "etag"
            }
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            System.Threading.CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
