using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using BBT.Workflow;
using BBT.Aether.Results;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Implementation of domain discovery resolver that resolves domain endpoints
/// from the service discovery registry. Every call queries the registry directly -
/// there is no cache, so a moved or de-registered endpoint is never masked behind a stale entry.
/// </summary>
public sealed class DomainDiscoveryResolver(
    IHttpClientFactory httpClientFactory,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<DomainDiscoveryResolver> logger) : IDomainDiscoveryResolver
{
    /// <inheritdoc />
    /// <remarks>
    /// Wrapped in a <c>Discovery.Resolve/{domain}</c> span (source
    /// <see cref="PipelineStepActivityHelper.ActivitySource"/>, already registered in every host's
    /// <c>Telemetry:Tracing:AdditionalSources</c>). This resolver is called from 32 sites across both
    /// hosts; a span here parents to whatever is ambient at each call site with no per-call-site
    /// changes, and the discovery HTTP call becomes its child instead of an unattributed HttpClient
    /// span. Started with the implicit-parent overload deliberately: an explicit
    /// <c>Activity.Current?.Context</c> leaves <c>Activity.Parent</c> null and severs the baggage
    /// chain (fixed for the event-hook span on this branch; not reintroduced here).
    /// </remarks>
    public async Task<Result<DiscoveryEndpoint>> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default)
    {
        using var activity = PipelineStepActivityHelper.ActivitySource.StartActivity(
            $"Discovery.Resolve/{domain}", ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.DiscoveryDomain, domain);
        activity?.SetTag(TelemetryConstants.TagNames.DiscoveryEndpointKind, preferredKind.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);

        var options = serviceDiscoveryOptions.Value;

        // If disabled, return failure (no fallback)
        if (!options.Enabled)
        {
            const string reason = "Service discovery is disabled";
            activity?.SetStatus(ActivityStatusCode.Error, reason);
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, reason));
        }

        var result = await QuerySingleDomainAsync(domain, preferredKind, cancellationToken);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
        }

        return result;
    }

    /// <summary>
    /// Queries a single domain from the discovery registry.
    /// Targets <see cref="ServiceDiscoveryOptions.DiscoveryEndpointTemplate"/>, which by default is
    /// the discovery domain's cached <c>domain-lookup</c> function rather than a direct instance read.
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

        var requestUrl = BuildSingleDomainUrl(options, domain);

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
                var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);
                logger.LogWarning(
                    "Discovery service returned {StatusCode} for domain '{Domain}': {Error}",
                    response.StatusCode, domain, errorContent);
                return Result<DiscoveryEndpoint>.Fail(
                    WorkflowErrors.DomainDiscoveryFailed(domain, $"HTTP {response.StatusCode}"));
            }

            var dto = await response.Content.ReadFromJsonAsync<SingleDomainResponse>(JsonSerializerConstants.JsonOptions, cancellationToken);

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
