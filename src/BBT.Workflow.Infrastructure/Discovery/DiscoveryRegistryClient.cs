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
}

/// <inheritdoc />
public sealed class DiscoveryRegistryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<DiscoveryRegistryClient> logger) : IDiscoveryRegistryClient
{
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
}
