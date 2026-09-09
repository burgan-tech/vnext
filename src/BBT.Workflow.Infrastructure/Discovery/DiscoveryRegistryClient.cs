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
    /// Reads every registration the registry holds, following its pagination.
    /// </summary>
    /// <returns>
    /// All accepted registrations; <c>DomainDiscoveryFailed</c> if ANY page fails.
    /// </returns>
    /// <remarks>
    /// All-or-nothing on purpose. A partially fetched list is indistinguishable from a registry that
    /// genuinely lost domains, and publishing one would evict good entries in favour of nothing. The
    /// previous implementation broke out of its page loop on error and cached what it had, which is
    /// how it came to hold only the first page — silently, for as long as the process lived.
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
        var accumulated = new List<DomainRegistration>();
        var previousPageKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applyFilter = !string.IsNullOrWhiteSpace(cache.BulkFilter);

        for (var page = 1; page <= cache.MaxPages; page++)
        {
            logger.FetchingDomainPage(page);

            var fetch = await FetchPageAsync(options, page, applyFilter, cancellationToken);

            // An older runtime rejects this filter shape with 4xx: its legacy path parses the status
            // through Enum.Parse, and InstanceStatus is a sealed class rather than an enum. Retry the
            // page unfiltered and drop the filter for the rest of the run — the client-side status
            // check below is authoritative anyway, so the filter was only ever an optimisation.
            if (fetch.FilterRejected)
            {
                logger.BulkFilterRejectedRetryingUnfiltered();
                applyFilter = false;
                fetch = await FetchPageAsync(options, page, applyFilter: false, cancellationToken);
            }

            if (!fetch.Succeeded)
                return Result<IReadOnlyList<DomainRegistration>>.Fail(fetch.Error);

            var items = fetch.Items;

            if (items.Count == 0)
                return Result<IReadOnlyList<DomainRegistration>>.Ok(accumulated);

            // A registry that ignores `page` would otherwise loop to MaxPages, re-adding the same
            // domains each time and burning the whole lease to achieve nothing.
            var pageKeys = items
                .Select(ResolveDomainName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (page > 1 && pageKeys.Count > 0 && pageKeys.SetEquals(previousPageKeys))
            {
                logger.BulkPaginationStalled(page);
                break;
            }

            previousPageKeys = pageKeys;

            foreach (var item in items)
            {
                if (ToRegistration(item, cache.AcceptedStatuses) is { } registration)
                    accumulated.Add(registration);
            }

            // A short page is the last page.
            if (items.Count < cache.BulkPageSize)
                return Result<IReadOnlyList<DomainRegistration>>.Ok(accumulated);

            if (page == cache.MaxPages)
            {
                // Loud on purpose. Silent truncation is exactly how the removed implementation ended
                // up holding only the first hundred domains, with nothing in the logs to say so.
                logger.BulkPageCapReached(cache.MaxPages, accumulated.Count);
            }
        }

        return Result<IReadOnlyList<DomainRegistration>>.Ok(accumulated);
    }

    /// <summary>
    /// Domain placeholder for errors raised by the bulk read, which is not about one domain.
    /// </summary>
    private const string AllDomains = "*";

    /// <summary>
    /// Outcome of one page fetch. <see cref="FilterRejected"/> is separate from failure because it
    /// is recoverable by retrying without the server-side filter.
    /// </summary>
    private readonly record struct PageFetch(
        bool Succeeded,
        bool FilterRejected,
        Error Error,
        List<FunctionDataItem> Items)
    {
        public static PageFetch Ok(List<FunctionDataItem> items) => new(true, false, Error.None, items);
        public static PageFetch Failed(Error error) => new(false, false, error, []);
        public static PageFetch RejectedFilter() => new(false, true, Error.None, []);
    }

    /// <summary>
    /// Fetches one page of registrations.
    /// </summary>
    private async Task<PageFetch> FetchPageAsync(
        ServiceDiscoveryOptions options,
        int page,
        bool applyFilter,
        CancellationToken cancellationToken)
    {
        var requestUrl = BuildBulkUrl(options, page, applyFilter);

        try
        {
            var httpClient = httpClientFactory.CreateClient(BulkHttpClientName);
            var response = await httpClient.GetAsync(requestUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);

                if (applyFilter && (int)response.StatusCode is >= 400 and < 500)
                    return PageFetch.RejectedFilter();

                logger.LogWarning(
                    "Discovery bulk read returned {StatusCode} for page {Page}: {Error}",
                    response.StatusCode, page, errorContent);

                return PageFetch.Failed(
                    WorkflowErrors.DomainDiscoveryFailed(AllDomains, $"HTTP {response.StatusCode}"));
            }

            var dto = await response.Content.ReadFromJsonAsync<FunctionDataListResponse>(
                JsonSerializerConstants.JsonOptions, cancellationToken);

            return PageFetch.Ok(dto?.Items ?? []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Discovery bulk read failed for page {Page}", page);
            return PageFetch.Failed(WorkflowErrors.DomainDiscoveryFailed(AllDomains, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during discovery bulk read, page {Page}", page);
            return PageFetch.Failed(WorkflowErrors.DomainDiscoveryFailed(AllDomains, ex.Message));
        }
    }

    /// <summary>
    /// The registry sets the instance key to the domain name, so it is a sound fallback for a
    /// registration whose attributes predate the <c>domainName</c> field.
    /// </summary>
    private static string ResolveDomainName(FunctionDataItem item)
        => string.IsNullOrWhiteSpace(item.Attributes.DomainName) ? item.Key : item.Attributes.DomainName;

    /// <summary>
    /// Maps one list item to a registration, or <c>null</c> when it should not be warmed.
    /// </summary>
    /// <remarks>
    /// The status check is client-side and authoritative, because the server-side filter is optional
    /// and may have been dropped (see <see cref="ListAllAsync"/>). Note that <c>metadata.status</c>
    /// is the status of the registration WORKFLOW INSTANCE and not a health signal — which is why
    /// the accepted set is configurable rather than hard-coded to <c>A</c>. A deployment whose
    /// registration flow runs through to a Finish state leaves its instances <c>C</c>, and hard-coding
    /// would silently skip every one of its domains: still correct, since a miss falls back to a live
    /// lookup, but the warm-up would achieve nothing at all.
    /// </remarks>
    private static DomainRegistration? ToRegistration(FunctionDataItem item, HashSet<string> acceptedStatuses)
    {
        if (!acceptedStatuses.Contains(item.Metadata.Status))
            return null;

        var domainName = ResolveDomainName(item);

        if (string.IsNullOrWhiteSpace(domainName))
            return null;

        var attributes = item.Attributes;

        return new DomainRegistration(
            domainName,
            string.IsNullOrWhiteSpace(attributes.BaseUrl) ? null : attributes.BaseUrl,
            string.IsNullOrWhiteSpace(attributes.AppId) ? null : attributes.AppId,
            attributes.HealthUrl);
    }

    /// <summary>
    /// Builds a bulk page URL.
    /// </summary>
    /// <remarks>
    /// The page number is carried explicitly rather than by following the response's
    /// <c>links.next</c>. Those links are generated with the REMOTE's API-gateway base path, which by
    /// design bears no relation to <see cref="ServiceDiscoveryOptions.BaseUrl"/>; a rooted link
    /// resolves against the authority alone and silently drops the configured path prefix. That is
    /// not hypothetical — it is how the removed implementation truncated itself to a single page.
    /// </remarks>
    private static string BuildBulkUrl(ServiceDiscoveryOptions options, int page, bool applyFilter)
    {
        var cache = options.Cache;

        var relativePath = string.Format(
            cache.BulkEndpointTemplate,
            Uri.EscapeDataString(options.Domain),
            page,
            cache.BulkPageSize);

        var url = options.BaseUrl.TrimEnd('/') + relativePath;

        if (applyFilter && !string.IsNullOrWhiteSpace(cache.BulkFilter))
            url += (url.Contains('?') ? "&" : "?") + "filter=" + Uri.EscapeDataString(cache.BulkFilter);

        return url;
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
    /// Response DTO for the paginated registration list.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <see cref="SingleDomainResponse"/>: the list endpoint answers
    /// <c>{ links, items: [ { key, metadata, attributes } ] }</c> while the single-domain lookup
    /// answers <c>{ data, eTag }</c>. Same registration, different envelope.
    /// </remarks>
    private sealed record FunctionDataListResponse
    {
        public List<FunctionDataItem> Items { get; init; } = [];
    }

    /// <summary>
    /// One registration in the paginated list. <c>attributes</c> carries the registration itself.
    /// </summary>
    private sealed record FunctionDataItem
    {
        public string Key { get; init; } = string.Empty;
        public FunctionData Attributes { get; init; } = new();
        public InstanceMetadata Metadata { get; init; } = new();
    }

    /// <summary>
    /// The subset of a list item's <c>metadata</c> the refresher reads.
    /// </summary>
    private sealed record InstanceMetadata
    {
        public string Status { get; init; } = string.Empty;
    }
}
