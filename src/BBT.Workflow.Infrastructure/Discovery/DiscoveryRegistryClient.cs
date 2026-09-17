using System.Net;
using System.Net.Http.Json;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// A domain's registration as stored in the discovery registry.
/// </summary>
/// <param name="DomainName">Registered domain name.</param>
/// <param name="BaseUrl">Public base URL. Required by the HTTP provider; optional under Dapr.</param>
/// <param name="AppId">Dapr app-id, when the domain registered one.</param>
/// <param name="HealthUrl">Health endpoint, informational here.</param>
public sealed record DomainRegistration(
    string DomainName,
    string? BaseUrl,
    string? AppId,
    string? HealthUrl);

/// <summary>
/// Reads domain registrations from the discovery registry.
/// </summary>
/// <remarks>
/// Extracted from the former single-class resolver so BOTH discovery providers can share one
/// registry read: the HTTP provider needs the <c>baseUrl</c>, and the Dapr provider needs only
/// the optional <c>appId</c> override. The registry itself — registration and health — is
/// unchanged by the Dapr migration; only address resolution moved.
/// </remarks>
public interface IDiscoveryRegistryClient
{
    /// <summary>
    /// Looks a domain up in the registry.
    /// </summary>
    /// <returns>
    /// The registration; <c>DomainEndpointNotFound</c> when the registry answers 404;
    /// <c>DomainDiscoveryFailed</c> for any other failure.
    /// </returns>
    Task<Result<DomainRegistration>> LookupAsync(string domain, CancellationToken cancellationToken);

    /// <summary>
    /// Reads every registration the registry holds, in one call to its <c>domain-list</c> function.
    /// </summary>
    /// <returns>
    /// Every registration the registry published; <c>DomainDiscoveryFailed</c> if the read fails.
    /// </returns>
    /// <remarks>
    /// All-or-nothing on purpose. A partially fetched list is indistinguishable from a registry that
    /// genuinely lost domains, and publishing one would evict good entries in favour of nothing —
    /// which is why a failed read returns an error rather than whatever it managed to collect.
    /// </remarks>
    Task<Result<IReadOnlyList<DomainRegistration>>> ListAllAsync(CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class DiscoveryRegistryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<DiscoveryRegistryClient> logger) : IDiscoveryRegistryClient
{
    /// <summary>
    /// Named <see cref="HttpClient"/> for the bulk refresh.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="DomainRegistrationService.HttpClientName"/>, even though the two
    /// carry identical settings. That client's Polly circuit breaker also guards the per-domain
    /// lookup — the path the cache falls back to. A bulk refresh failing on every tick would trip
    /// that breaker and take the fallback down with it, so the refresh would break the very thing it
    /// degrades into. Separate client, separate breaker.
    /// </remarks>
    public const string BulkHttpClientName = "ServiceDiscoveryBulk";

    /// <inheritdoc />
    public async Task<Result<DomainRegistration>> LookupAsync(
        string domain,
        CancellationToken cancellationToken)
    {
        var options = serviceDiscoveryOptions.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Result<DomainRegistration>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, "Discovery base URL not configured"));
        }

        logger.QueryingSingleDomain(domain);

        var requestUrl = BuildSingleDomainUrl(options, domain);

        try
        {
            var httpClient = httpClientFactory.CreateClient(DomainRegistrationService.HttpClientName);
            var response = await httpClient.GetAsync(requestUrl, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("Domain '{Domain}' not found in service discovery registry", domain);
                return Result<DomainRegistration>.Fail(WorkflowErrors.DomainEndpointNotFound(domain));
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);
                logger.LogWarning(
                    "Discovery service returned {StatusCode} for domain '{Domain}': {Error}",
                    response.StatusCode, domain, errorContent);
                return Result<DomainRegistration>.Fail(
                    WorkflowErrors.DomainDiscoveryFailed(domain, $"HTTP {response.StatusCode}"));
            }

            var dto = await response.Content.ReadFromJsonAsync<SingleDomainResponse>(
                JsonSerializerConstants.JsonOptions, cancellationToken);

            if (dto?.Data is null || string.IsNullOrWhiteSpace(dto.Data.DomainName))
            {
                return Result<DomainRegistration>.Fail(
                    WorkflowErrors.DomainDiscoveryFailed(domain, "Empty or invalid response"));
            }

            // baseUrl is NOT required here. Under the Dapr provider a registration carrying only
            // domainName + appId is entirely valid; requiring a URL is the HTTP provider's rule
            // and is enforced there, where it actually matters.
            return Result.Ok(new DomainRegistration(
                dto.Data.DomainName,
                string.IsNullOrWhiteSpace(dto.Data.BaseUrl) ? null : dto.Data.BaseUrl,
                string.IsNullOrWhiteSpace(dto.Data.AppId) ? null : dto.Data.AppId,
                dto.Data.HealthUrl));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HTTP request failed for domain '{Domain}'", domain);
            return Result<DomainRegistration>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error querying domain '{Domain}'", domain);
            return Result<DomainRegistration>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, ex.Message));
        }
    }

    /// <summary>
    /// Builds the single-domain registry URL from <see cref="ServiceDiscoveryOptions.DiscoveryEndpointTemplate"/>.
    /// The domain is URL-encoded: the default template carries it as a query-string value
    /// (<c>?key={0}</c>), so an unencoded name would break the request rather than 404.
    /// </summary>
    private static string BuildSingleDomainUrl(ServiceDiscoveryOptions options, string domain)
    {
        var relativePath = string.Format(options.DiscoveryEndpointTemplate, Uri.EscapeDataString(domain));
        return options.BaseUrl.TrimEnd('/') + relativePath;
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<DomainRegistration>>> ListAllAsync(
        CancellationToken cancellationToken)
    {
        var options = serviceDiscoveryOptions.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Result<IReadOnlyList<DomainRegistration>>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(AllDomains, "Discovery base URL not configured"));
        }

        var cache = options.Cache;
        var requestUrl = BuildDomainListUrl(options);

        logger.FetchingDomainList(requestUrl);

        try
        {
            var httpClient = httpClientFactory.CreateClient(BulkHttpClientName);
            var response = await httpClient.GetAsync(requestUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);

                // Called out separately because it is the one failure with a configuration cause
                // rather than an operational one, and the message is the only guidance left now that
                // the paginated instance-list fallback is gone.
                if (response.StatusCode == HttpStatusCode.NotFound)
                    logger.DomainListEndpointMissing(requestUrl);
                else
                    logger.LogWarning(
                        "Discovery domain-list read returned {StatusCode}: {Error}",
                        response.StatusCode, errorContent);

                return Result<IReadOnlyList<DomainRegistration>>.Fail(
                    WorkflowErrors.DomainDiscoveryFailed(AllDomains, $"HTTP {response.StatusCode}"));
            }

            var dto = await response.Content.ReadFromJsonAsync<DomainListResponse>(
                JsonSerializerConstants.JsonOptions, cancellationToken);

            var items = dto?.Items ?? [];

            // The registry function serves ONE page, sized by its own ceiling, and its response
            // carries no truncation signal. Sitting exactly on the expected maximum is therefore the
            // only evidence available that domains may be missing — loud, because the list looks
            // perfectly normal and would otherwise be published and cached as complete.
            if (items.Count >= cache.DomainListExpectedMax)
                logger.DomainListCeilingReached(items.Count, cache.DomainListExpectedMax);

            var registrations = new List<DomainRegistration>(items.Count);

            foreach (var item in items)
            {
                if (ToRegistration(item) is { } registration)
                    registrations.Add(registration);
            }

            return Result<IReadOnlyList<DomainRegistration>>.Ok(registrations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Discovery domain-list read failed");
            return Result<IReadOnlyList<DomainRegistration>>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(AllDomains, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during the discovery domain-list read");
            return Result<IReadOnlyList<DomainRegistration>>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(AllDomains, ex.Message));
        }
    }

    /// <summary>
    /// Domain placeholder for errors raised by the bulk read, which is not about one domain.
    /// </summary>
    private const string AllDomains = "*";

    /// <summary>
    /// Builds the bulk-read URL from <see cref="DiscoveryCacheOptions.DomainListEndpointTemplate"/>.
    /// </summary>
    /// <remarks>
    /// The registry's <c>domain-list</c> is a Domain-scope function, so the whole registry fits in
    /// one request and there is no pagination to drive: status filtering, ordering and the
    /// key-versus-<c>domainName</c> decision all happen server-side, where the registry owns them.
    /// </remarks>
    private static string BuildDomainListUrl(ServiceDiscoveryOptions options)
    {
        var relativePath = string.Format(
            options.Cache.DomainListEndpointTemplate,
            Uri.EscapeDataString(options.Domain));

        return options.BaseUrl.TrimEnd('/') + relativePath;
    }

    /// <summary>
    /// Maps one listed domain to a registration, or <c>null</c> when it cannot be warmed.
    /// </summary>
    /// <remarks>
    /// Only the name is required here. A registration with no <c>baseUrl</c> is already dropped by
    /// the registry function, and requiring one again would be the HTTP provider's rule leaking into
    /// a shared read — the same reasoning as <see cref="LookupAsync"/>.
    /// </remarks>
    private static DomainRegistration? ToRegistration(FunctionData item)
    {
        if (string.IsNullOrWhiteSpace(item.DomainName))
            return null;

        return new DomainRegistration(
            item.DomainName,
            string.IsNullOrWhiteSpace(item.BaseUrl) ? null : item.BaseUrl,
            string.IsNullOrWhiteSpace(item.AppId) ? null : item.AppId,
            item.HealthUrl);
    }

    /// <summary>
    /// Function data containing domain registration details.
    /// </summary>
    private sealed record FunctionData
    {
        public string DomainName { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string? AppId { get; init; }
        public string? HealthUrl { get; init; }
    }

    /// <summary>
    /// Response DTO for single domain query.
    /// </summary>
    private sealed record SingleDomainResponse
    {
        public FunctionData Data { get; init; } = new();
        public string ETag { get; init; } = string.Empty;
        public Dictionary<string, object>? Extensions { get; init; }
    }

    /// <summary>
    /// Response DTO for the registry's <c>domain-list</c> function.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="SingleDomainResponse"/>: the list function answers
    /// <c>{ items: [ { domainName, baseUrl, appId, healthUrl } ] }</c> while the single-domain lookup
    /// answers <c>{ data, eTag }</c>. Same registration, different envelope — and an empty
    /// <c>items</c> is a valid 200, never a 404.
    /// </remarks>
    private sealed record DomainListResponse
    {
        public List<FunctionData> Items { get; init; } = [];
    }
}
