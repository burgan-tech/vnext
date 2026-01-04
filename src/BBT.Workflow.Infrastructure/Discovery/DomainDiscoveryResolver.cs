using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BBT.Aether.DistributedCache;
using BBT.Workflow.Remote.Configuration;
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
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    IOptions<RemoteOptions> remoteOptions,
    ILogger<DomainDiscoveryResolver> logger) : IDomainDiscoveryResolver
{
    private const string CacheKeyPrefix = "discovery:endpoint:";
    private const string IfNoneMatchHeader = "If-None-Match";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public async Task<DiscoveryEndpoint> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default)
    {
        var options = serviceDiscoveryOptions.Value;

        // If service discovery is disabled, fall back to static BaseUrl
        // if (!options.Enabled)
        // {
        //     return GetFallbackEndpoint(domain);
        // }

        // Try to get from cache first
        var cacheKey = GetCacheKey(domain);
        var cachedEntry = await TryGetFromCacheAsync(cacheKey, cancellationToken);

        // If we have a cached entry with ETag, use conditional request
        if (cachedEntry is not null)
        {
            var fetchResult = await FetchFromDiscoveryWithETagAsync(
                domain, preferredKind, cachedEntry.ETag, cancellationToken);

            // 304 Not Modified - use cached data
            if (fetchResult.IsNotModified)
            {
                logger.LogDebug(
                    "Endpoint for domain '{Domain}' not modified (304), using cache: {BaseUrl}",
                    domain, cachedEntry.BaseUrl);

                return new DiscoveryEndpoint(
                    cachedEntry.Kind,
                    new Uri(cachedEntry.BaseUrl),
                    cachedEntry.DaprAppId);
            }

            // Got new data, cache it
            if (fetchResult.Endpoint is not null)
            {
                await CacheEndpointAsync(cacheKey, fetchResult.Endpoint, fetchResult.ETag, options.DiscoveryCacheSeconds, cancellationToken);

                logger.LogInformation(
                    "Endpoint for domain '{Domain}' updated from discovery: {BaseUrl}, Kind: {Kind}",
                    domain, fetchResult.Endpoint.BaseUrl, fetchResult.Endpoint.Kind);

                return fetchResult.Endpoint;
            }

            // Fallback case - use cached data
            return new DiscoveryEndpoint(
                cachedEntry.Kind,
                new Uri(cachedEntry.BaseUrl),
                cachedEntry.DaprAppId);
        }

        // No cache - fetch fresh
        var freshResult = await FetchFromDiscoveryWithETagAsync(domain, preferredKind, null, cancellationToken);

        if (freshResult.Endpoint is not null)
        {
            await CacheEndpointAsync(cacheKey, freshResult.Endpoint, freshResult.ETag, options.DiscoveryCacheSeconds, cancellationToken);

            logger.LogInformation(
                "Resolved endpoint for domain '{Domain}' from discovery service: {BaseUrl}, Kind: {Kind}",
                domain, freshResult.Endpoint.BaseUrl, freshResult.Endpoint.Kind);

            return freshResult.Endpoint;
        }

        // All failed - use fallback
        return GetFallbackEndpoint(domain);
    }

    /// <summary>
    /// Gets the fallback endpoint when service discovery is disabled.
    /// Uses the static BaseUrl from RemoteOptions configuration.
    /// </summary>
    private DiscoveryEndpoint GetFallbackEndpoint(string domain)
    {
        var fallbackBaseUrl = remoteOptions.Value.BaseUrl;

        if (string.IsNullOrWhiteSpace(fallbackBaseUrl))
        {
            throw new InvalidOperationException(
                $"Cannot resolve endpoint for domain '{domain}': " +
                "ServiceDiscovery.Enabled is false and vNextApi.BaseUrl is not configured.");
        }

        logger.LogDebug(
            "Service discovery disabled, using fallback BaseUrl for domain '{Domain}': {BaseUrl}",
            domain, fallbackBaseUrl);

        return new DiscoveryEndpoint(
            EndpointKind.Url,
            new Uri(fallbackBaseUrl.TrimEnd('/') + "/"));
    }

    /// <summary>
    /// Tries to get the endpoint from distributed cache.
    /// Returns the cached entry including ETag for conditional requests.
    /// </summary>
    private async Task<CachedEndpoint?> TryGetFromCacheAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            return await distributedCache.GetAsync<CachedEndpoint>(cacheKey, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get endpoint from cache with key '{CacheKey}'", cacheKey);
        }

        return null;
    }

    /// <summary>
    /// Caches the endpoint in distributed cache with ETag for conditional requests.
    /// </summary>
    private async Task CacheEndpointAsync(
        string cacheKey,
        DiscoveryEndpoint endpoint,
        string? eTag,
        int ttlSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var cached = new CachedEndpoint(
                endpoint.Kind,
                endpoint.BaseUrl.ToString(),
                endpoint.DaprAppId,
                eTag);

            var cacheOptions = new DistributedCacheEntryOptions
            {
                AbsoluteExpiration = DateTimeOffset.UtcNow.AddSeconds(Math.Max(10, ttlSeconds))
            };

            await distributedCache.SetAsync(
                cacheKey,
                cached,
                cacheOptions,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache endpoint with key '{CacheKey}'", cacheKey);
        }
    }

    /// <summary>
    /// Fetches the endpoint from the discovery service API with ETag support.
    /// Sends If-None-Match header if cachedETag is provided.
    /// </summary>
    private async Task<DiscoveryFetchResult> FetchFromDiscoveryWithETagAsync(
        string domain,
        EndpointKind preferredKind,
        string? cachedETag,
        CancellationToken cancellationToken)
    {
        var options = serviceDiscoveryOptions.Value;

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException(
                $"Cannot resolve endpoint for domain '{domain}': " +
                "ServiceDiscovery.BaseUrl is not configured but Enabled is true.");
        }

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
                request.Headers.TryAddWithoutValidation(IfNoneMatchHeader,  $"\"{cachedETag}\"");
            }

            var response = await httpClient.SendAsync(request, cancellationToken);

            // Handle 304 Not Modified
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                logger.LogDebug(
                    "Discovery service returned 304 Not Modified for domain '{Domain}'",
                    domain);

                return DiscoveryFetchResult.NotModified();
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Discovery service returned {StatusCode} for domain '{Domain}': {Error}",
                    response.StatusCode, domain, errorContent);

                return DiscoveryFetchResult.Failed();
            }

            var dto = await response.Content.ReadFromJsonAsync<DiscoveryRegistrationDto>(JsonOptions, cancellationToken);

            if (dto?.Data is null || string.IsNullOrWhiteSpace(dto.Data.BaseUrl))
            {
                logger.LogWarning(
                    "Discovery service returned empty or invalid registration for domain '{Domain}'",
                    domain);

                return DiscoveryFetchResult.Failed();
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

            // Get ETag from response header (includes quotes per RFC 7232)
            // or fallback to DTO's ETag (may or may not include quotes)
            string? responseETag = null;
            
            if (response.Headers.ETag is not null)
            {
                // Header ETag includes quotes: "value"
                responseETag = response.Headers.ETag.Tag;
            }
            else if (!string.IsNullOrWhiteSpace(dto.ETag))
            {
                // DTO ETag may not include quotes - normalize to quoted format
                responseETag = dto.ETag.Trim('"');
            }

            return DiscoveryFetchResult.Success(endpoint, responseETag);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Failed to fetch endpoint from discovery service for domain '{Domain}'.",
                domain);

            return DiscoveryFetchResult.Failed();
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            logger.LogWarning(ex,
                "Discovery service request timed out for domain '{Domain}'.",
                domain);

            return DiscoveryFetchResult.Failed();
        }
    }

    private static string GetCacheKey(string domain) => $"{CacheKeyPrefix}{domain}";

    /// <summary>
    /// Internal record for caching endpoint data in distributed cache.
    /// Includes ETag for conditional request support.
    /// </summary>
    private sealed record CachedEndpoint(
        EndpointKind Kind,
        string BaseUrl,
        string? DaprAppId,
        string? ETag);

    /// <summary>
    /// Result of fetching endpoint from discovery service.
    /// Supports 304 Not Modified responses.
    /// </summary>
    private sealed record DiscoveryFetchResult(
        bool IsNotModified,
        DiscoveryEndpoint? Endpoint,
        string? ETag)
    {
        public static DiscoveryFetchResult NotModified() => new(true, null, null);
        public static DiscoveryFetchResult Failed() => new(false, null, null);
        public static DiscoveryFetchResult Success(DiscoveryEndpoint endpoint, string? eTag) => new(false, endpoint, eTag);
    }

    /// <summary>
    /// DTO for discovery registration response from the discovery service.
    /// Maps to the function data response returned by the domain workflow.
    /// </summary>
    private sealed record DiscoveryRegistrationDto
    {
        /// <summary>
        /// Data containing endpoint information.
        /// </summary>
        public DiscoveryDataDto Data { get; init; } = new();

        /// <summary>
        /// ETag for optimistic concurrency (includes quotes per RFC 7232).
        /// </summary>
        public string ETag { get; init; } = string.Empty;

        /// <summary>
        /// Extensions dictionary for additional metadata.
        /// </summary>
        public Dictionary<string, object>? Extensions { get; init; }
    }

    /// <summary>
    /// Data of a discovery registration containing endpoint details.
    /// </summary>
    private sealed record DiscoveryDataDto
    {
        /// <summary>
        /// The registered domain name.
        /// </summary>
        public string DomainName { get; init; } = string.Empty;

        /// <summary>
        /// Base URL for the domain's API.
        /// </summary>
        public string BaseUrl { get; init; } = string.Empty;

        /// <summary>
        /// Health check URL for the domain.
        /// </summary>
        public string HealthUrl { get; init; } = string.Empty;

        /// <summary>
        /// Dapr application ID for service invocation.
        /// </summary>
        public string AppId { get; init; } = string.Empty;

        /// <summary>
        /// ETag of the data (may differ from response ETag).
        /// </summary>
        public string ETag { get; init; } = string.Empty;
    }
}

