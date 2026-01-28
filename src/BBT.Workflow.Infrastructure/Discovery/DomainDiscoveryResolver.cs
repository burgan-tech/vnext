using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using BBT.Aether.DistributedCache;
using BBT.Aether.DistributedLock;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Implementation of domain discovery resolver that resolves domain endpoints
/// from the service discovery registry with distributed caching support.
/// Supports ETag-based conditional requests for efficient cache validation.
/// </summary>
public sealed class DomainDiscoveryResolver(
    IHttpClientFactory httpClientFactory,
    IDistributedCacheService distributedCache,
    IDistributedLockService lockService,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<DomainDiscoveryResolver> logger) : IDomainDiscoveryResolver
{
    private const string BulkCacheKey = "discovery:domains:bulk";
    private const string BulkCacheLockKey = "discovery:bulk-lock";
    private const int LockExpiryInSeconds = 30;
    private const string IfNoneMatchHeader = "If-None-Match";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public async Task<Result<DiscoveryEndpoint>> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default)
    {
        var options = serviceDiscoveryOptions.Value;

        // If disabled, return failure (no fallback)
        if (!options.Enabled)
        {
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, "Service discovery is disabled"));
        }

        // 1. Try bulk cache first
        var bulkCache = await distributedCache.GetAsync<BulkDomainCache>(BulkCacheKey, cancellationToken);

        if (bulkCache is { Items.Count: > 0 })
        {
            var domainItem = bulkCache.Items.FirstOrDefault(i => 
                i.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase));

            if (domainItem != null)
            {
                // Found in cache - perform ETag check
                var etagCheckResult = await CheckDomainETagAsync(
                    domain, domainItem.ItemETag, preferredKind, cancellationToken);

                if (etagCheckResult.IsNotModified)
                {
                    // Use cached endpoint
                    var kind = DetermineEndpointKind(preferredKind, domainItem.AppId);
                    var baseUrl = domainItem.BaseUrl.TrimEnd('/') + "/";
                    return Result.Ok(new DiscoveryEndpoint(kind, new Uri(baseUrl), domainItem.AppId));
                }
                else if (etagCheckResult.Endpoint != null)
                {
                    // ETag changed - update bulk cache
                    await UpdateDomainInBulkCacheAsync(
                        domain, etagCheckResult.Endpoint, etagCheckResult.ETag, cancellationToken);
                    return Result.Ok(etagCheckResult.Endpoint);
                }
                // If ETag check failed, fall through to direct query
            }
        }

        // 2. Not in bulk cache - query registry directly
        logger.DomainNotFoundInCache(domain);
        var queryResult = await QuerySingleDomainAsync(domain, preferredKind, cancellationToken);

        if (queryResult.IsSuccess)
        {
            // Add to bulk cache for future requests
            await AddDomainToBulkCacheAsync(domain, queryResult.Value!, cancellationToken);
        }

        return queryResult;
    }

    /// <summary>
    /// Queries a single domain from the discovery registry.
    /// </summary>
    private async Task<Result<DiscoveryEndpoint>> QuerySingleDomainAsync(
        string domain,
        EndpointKind preferredKind,
        CancellationToken cancellationToken)
    {
        var options = serviceDiscoveryOptions.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, "Discovery base URL not configured"));
        }

        logger.QueryingSingleDomain(domain);

        var endpointTemplate = options.DiscoveryEndpointTemplate;
        var relativePath = string.Format(endpointTemplate, domain);
        var requestUrl = options.BaseUrl.TrimEnd('/') + relativePath;

        try
        {
            var httpClient = httpClientFactory.CreateClient(DomainRegistrationService.HttpClientName);
            var response = await httpClient.GetAsync(requestUrl, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogWarning("Domain '{Domain}' not found in service discovery registry", domain);
                return Result<DiscoveryEndpoint>.Fail(WorkflowErrors.DomainEndpointNotFound(domain));
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Discovery service returned {StatusCode} for domain '{Domain}': {Error}",
                    response.StatusCode, domain, errorContent);
                return Result<DiscoveryEndpoint>.Fail(
                    WorkflowErrors.DomainDiscoveryFailed(domain, $"HTTP {response.StatusCode}"));
            }

            var dto = await response.Content.ReadFromJsonAsync<SingleDomainResponse>(JsonOptions, cancellationToken);

            if (dto?.Data is null || string.IsNullOrWhiteSpace(dto.Data.BaseUrl))
            {
                return Result<DiscoveryEndpoint>.Fail(
                    WorkflowErrors.DomainDiscoveryFailed(domain, "Empty or invalid response"));
            }

            var kind = DetermineEndpointKind(preferredKind, dto.Data.AppId);
            var baseUrl = dto.Data.BaseUrl.TrimEnd('/') + "/";
            var endpoint = new DiscoveryEndpoint(kind, new Uri(baseUrl), dto.Data.AppId);

            logger.DomainResolvedFromRegistry(domain, baseUrl);

            return Result.Ok(endpoint);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "HTTP request failed for domain '{Domain}'", domain);
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, ex.Message));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error querying domain '{Domain}'", domain);
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, ex.Message));
        }
    }

    /// <summary>
    /// Determines the endpoint kind based on preference and available data.
    /// </summary>
    private EndpointKind DetermineEndpointKind(EndpointKind preferredKind, string? appId)
    {
        if (preferredKind == EndpointKind.Dapr && string.IsNullOrWhiteSpace(appId))
        {
            logger.LogDebug("Requested Dapr endpoint but AppId not available, falling back to URL");
            return EndpointKind.Url;
        }
        return preferredKind;
    }

    /// <summary>
    /// Adds a domain to the bulk cache after it's been queried individually.
    /// </summary>
    private async Task AddDomainToBulkCacheAsync(
        string domain,
        DiscoveryEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var bulkCache = await distributedCache.GetAsync<BulkDomainCache>(BulkCacheKey, cancellationToken) 
                ?? new BulkDomainCache { Items = new List<DomainEndpointItem>() };

            var newItem = new DomainEndpointItem
            {
                DomainName = domain,
                BaseUrl = endpoint.BaseUrl.ToString().TrimEnd('/'),
                AppId = endpoint.DaprAppId,
                ItemETag = null // No ETag on first add
            };

            var updatedItems = bulkCache.Items
                .Where(i => !i.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase))
                .Append(newItem)
                .ToList();

            var updatedCache = bulkCache with { Items = updatedItems };

            var options = serviceDiscoveryOptions.Value;
            await distributedCache.SetAsync(
                BulkCacheKey,
                updatedCache,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(options.DiscoveryCacheSeconds)
                },
                cancellationToken);

            logger.LogDebug("Added domain '{Domain}' to bulk cache", domain);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add domain '{Domain}' to bulk cache", domain);
        }
    }

    /// <inheritdoc />
    public async Task RefreshBulkCacheAsync(CancellationToken cancellationToken = default)
    {
        var options = serviceDiscoveryOptions.Value;

        if (!options.Enabled)
        {
            logger.LogDebug("Service discovery is disabled. Skipping bulk cache refresh");
            return;
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            logger.BulkCacheRefreshFailed("ServiceDiscovery.BaseUrl is not configured");
            return;
        }

        // Try to acquire distributed lock to prevent concurrent updates
        var lockAcquired = await lockService.ExecuteWithLockAsync(
            BulkCacheLockKey,
            async () =>
            {
                logger.BulkCacheRefreshStarted();

                // Fetch all pages with pagination
                var allItems = await FetchAllPagesAsync(cancellationToken);

                if (allItems.Count == 0)
                {
                    logger.BulkCacheRefreshed(0);
                    return;
                }

                // Build bulk cache model
                var bulkCache = new BulkDomainCache
                {
                    Items = allItems.Select(item => new DomainEndpointItem
                    {
                        DomainName = item.Data.DomainName,
                        BaseUrl = item.Data.BaseUrl,
                        AppId = item.Data.AppId,
                        ItemETag = item.Etag
                    }).ToList(),
                    BulkETag = null // Will be set from response header if needed
                };

                // Store in distributed cache
                await distributedCache.SetAsync(
                    BulkCacheKey,
                    bulkCache,
                    new DistributedCacheEntryOptions
                    {
                        AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(options.DiscoveryCacheSeconds)
                    },
                    cancellationToken);

                logger.BulkCacheRefreshed(bulkCache.Items.Count);
            },
            LockExpiryInSeconds,
            cancellationToken);

        if (!lockAcquired)
        {
            logger.LogWarning("Could not acquire lock for bulk cache refresh, another instance is refreshing");
        }
    }

    /// <summary>
    /// Fetches all pages of domain registrations from the discovery service.
    /// Handles pagination using the links.next field.
    /// </summary>
    private async Task<List<FunctionDataItem>> FetchAllPagesAsync(CancellationToken cancellationToken)
    {
        var options = serviceDiscoveryOptions.Value;
        var allItems = new List<FunctionDataItem>();
        var currentPage = 1;

        // Build initial URL with pagination and filter
        var registryBaseUrl = options.BaseUrl.TrimEnd('/');
        var registryDomain = options.Domain;
        var filter = HttpUtility.UrlEncode("{\"Status\": \"A\"}");
        var initialUrl = $"{registryBaseUrl}/{registryDomain}/workflows/domain/functions/data?page=1&pageSize=100&filter={filter}";

        string? nextUrl = initialUrl;

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            logger.FetchingDomainPage(currentPage);

            try
            {
                var httpClient = httpClientFactory.CreateClient(DomainRegistrationService.HttpClientName);
                var response = await httpClient.GetFromJsonAsync<FunctionDataListResponse>(nextUrl, JsonOptions, cancellationToken);

                if (response?.Items is { Count: > 0 })
                {
                    allItems.AddRange(response.Items);
                }

                // Get next page URL
                nextUrl = response?.Links.Next;
                currentPage++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch page {Page} of domain registrations", currentPage);
                break; // Stop pagination on error
            }
        }

        return allItems;
    }

    /// <summary>
    /// Checks if a domain's ETag is still valid using conditional GET request.
    /// Returns 304 if not modified, or new endpoint data if changed.
    /// </summary>
    private async Task<ETagCheckResult> CheckDomainETagAsync(
        string domain,
        string? cachedETag,
        EndpointKind preferredKind,
        CancellationToken cancellationToken)
    {
        var options = serviceDiscoveryOptions.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return ETagCheckResult.Failed();
        }

        // Use the old endpoint template for single domain check
        var endpointTemplate = options.DiscoveryEndpointTemplate;
        var relativePath = string.Format(endpointTemplate, domain);
        var requestUrl = options.BaseUrl.TrimEnd('/') + relativePath;

        try
        {
            var httpClient = httpClientFactory.CreateClient(DomainRegistrationService.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

            // Add If-None-Match header for conditional request
            if (!string.IsNullOrWhiteSpace(cachedETag))
            {
                request.Headers.TryAddWithoutValidation(IfNoneMatchHeader, $"\"{cachedETag}\"");
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            // Handle 304 Not Modified
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                logger.LogDebug(
                    "Discovery service returned 304 Not Modified for domain '{Domain}'",
                    domain);

                return ETagCheckResult.NotModified();
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Discovery service returned {StatusCode} for domain '{Domain}': {Error}",
                    response.StatusCode, domain, errorContent);

                return ETagCheckResult.Failed();
            }

            var dto = await response.Content.ReadFromJsonAsync<SingleDomainResponse>(JsonOptions, cancellationToken);

            if (dto?.Data is null || string.IsNullOrWhiteSpace(dto.Data.BaseUrl))
            {
                logger.LogWarning(
                    "Discovery service returned empty or invalid registration for domain '{Domain}'",
                    domain);

                return ETagCheckResult.Failed();
            }

            // Determine endpoint kind based on available data and preference
            var kind = preferredKind;
            if (preferredKind == EndpointKind.Dapr && string.IsNullOrWhiteSpace(dto.Data.AppId))
            {
                kind = EndpointKind.Url;
                logger.LogDebug(
                    "Requested Dapr endpoint but AppId not available for domain '{Domain}', falling back to URL",
                    domain);
            }

            var baseUrl = dto.Data.BaseUrl.TrimEnd('/') + "/";

            var endpoint = new DiscoveryEndpoint(
                kind,
                new Uri(baseUrl),
                dto.Data.AppId);

            // Get ETag from response header or DTO
            string? responseETag = null;

            if (response.Headers.ETag is not null)
            {
                responseETag = response.Headers.ETag.Tag.Trim('"');
            }
            else if (!string.IsNullOrWhiteSpace(dto.Etag))
            {
                responseETag = dto.Etag.Trim('"');
            }

            return ETagCheckResult.Success(endpoint, responseETag);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Failed to check ETag for domain '{Domain}'.",
                domain);

            return ETagCheckResult.Failed();
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            logger.LogWarning(ex,
                "ETag check request timed out for domain '{Domain}'.",
                domain);

            return ETagCheckResult.Failed();
        }
    }

    /// <summary>
    /// Updates a single domain's information in the bulk cache.
    /// </summary>
    private async Task UpdateDomainInBulkCacheAsync(
        string domain,
        DiscoveryEndpoint endpoint,
        string? newETag,
        CancellationToken cancellationToken)
    {
        try
        {
            var bulkCache = await distributedCache.GetAsync<BulkDomainCache>(BulkCacheKey, cancellationToken);

            if (bulkCache == null)
            {
                logger.LogWarning("Cannot update domain '{Domain}' in bulk cache: cache not found", domain);
                return;
            }

            // Find and update the domain item
            var updatedItems = bulkCache.Items.Select(item =>
            {
                if (item.DomainName.Equals(domain, StringComparison.OrdinalIgnoreCase))
                {
                    return new DomainEndpointItem
                    {
                        DomainName = domain,
                        BaseUrl = endpoint.BaseUrl.ToString().TrimEnd('/'),
                        AppId = endpoint.DaprAppId,
                        ItemETag = newETag
                    };
                }
                return item;
            }).ToList();

            var updatedCache = bulkCache with { Items = updatedItems };

            var options = serviceDiscoveryOptions.Value;
            await distributedCache.SetAsync(
                BulkCacheKey,
                updatedCache,
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(options.DiscoveryCacheSeconds)
                },
                cancellationToken);

            logger.LogDebug("Updated domain '{Domain}' in bulk cache with new ETag", domain);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update domain '{Domain}' in bulk cache", domain);
        }
    }

    /// <summary>
    /// Bulk cache model containing all domain endpoints.
    /// </summary>
    private sealed record BulkDomainCache
    {
        public List<DomainEndpointItem> Items { get; init; } = new();
        public string? BulkETag { get; init; }
    }

    /// <summary>
    /// Individual domain endpoint item in the bulk cache.
    /// </summary>
    private sealed record DomainEndpointItem
    {
        public string DomainName { get; init; } = string.Empty;
        public string BaseUrl { get; init; } = string.Empty;
        public string? AppId { get; init; }
        public string? ItemETag { get; init; }
    }

    /// <summary>
    /// Response DTO for paginated function data list.
    /// </summary>
    private sealed record FunctionDataListResponse
    {
        public PaginationLinks Links { get; init; } = new();
        public List<FunctionDataItem> Items { get; init; } = new();
    }

    /// <summary>
    /// Pagination links for navigating through pages.
    /// </summary>
    private sealed record PaginationLinks
    {
        public string? Self { get; init; }
        public string? First { get; init; }
        public string? Next { get; init; }
        public string? Prev { get; init; }
    }

    /// <summary>
    /// Individual function data item from the registry.
    /// </summary>
    private sealed record FunctionDataItem
    {
        public FunctionData Data { get; init; } = new();
        public string Etag { get; init; } = string.Empty;
        public Dictionary<string, object>? Extensions { get; init; }
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
    /// Result of ETag check for a single domain.
    /// </summary>
    private sealed record ETagCheckResult(
        bool IsNotModified,
        DiscoveryEndpoint? Endpoint,
        string? ETag)
    {
        public static ETagCheckResult NotModified() => new(true, null, null);
        public static ETagCheckResult Failed() => new(false, null, null);
        public static ETagCheckResult Success(DiscoveryEndpoint endpoint, string? eTag) => new(false, endpoint, eTag);
    }

    /// <summary>
    /// Response DTO for single domain query (old endpoint).
    /// </summary>
    private sealed record SingleDomainResponse
    {
        public FunctionData Data { get; init; } = new();
        public string Etag { get; init; } = string.Empty;
        public Dictionary<string, object>? Extensions { get; init; }
    }
}

