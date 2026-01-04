using System.Net.Http.Json;
using System.Text.Json;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Service implementation for registering the current domain with the central service registry.
/// This service makes an HTTP call to the registry endpoint to start the domain-registration workflow,
/// which registers domain information including domain name, base URL, and health URL for service discovery purposes.
/// </summary>
public sealed class DomainRegistrationService(
    IHttpClientFactory httpClientFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    IConfiguration configuration,
    ILogger<DomainRegistrationService> logger) : IDomainRegistrationService
{
    /// <summary>
    /// HTTP client name for service discovery operations.
    /// </summary>
    public const string HttpClientName = "ServiceDiscovery";
    private const string VNextApiBaseUrlKey = "vNextApi:BaseUrl";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <inheritdoc />
    public async Task RegisterDomainAsync(CancellationToken cancellationToken = default)
    {
        var options = serviceDiscoveryOptions.Value;
        var vNextApiBaseUrl = configuration[VNextApiBaseUrlKey];

        if (string.IsNullOrWhiteSpace(vNextApiBaseUrl))
        {
            logger.LogWarning(
                "Domain registration skipped: '{ConfigKey}' is not configured",
                VNextApiBaseUrlKey);
            return;
        }

        var domainName = runtimeInfoProvider.Domain;
        var baseUrl = vNextApiBaseUrl.TrimEnd('/');
        var healthUrl = $"{baseUrl}/health";
        var appId = configuration["DAPR_APP_ID"];
        
        logger.LogInformation(
            "Starting domain registration for domain '{DomainName}' with baseUrl '{BaseUrl}' and healthUrl '{HealthUrl}' to registry '{RegistryUrl}'",
            domainName, baseUrl, healthUrl, options.BaseUrl);

        var requestBody = new
        {
            key = $"domain-registration-{domainName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            tags = new[] { "domain", "registration", "startup" },
            attributes = new
            {
                domainName,
                baseUrl,
                healthUrl,
                appId
            }
        };

        var registryBaseUrl = options.BaseUrl.TrimEnd('/');
        var registryDomain = options.Domain;
        var registryFlow= options.RegistryFlow;
        var requestUrl = $"{registryBaseUrl}/{registryDomain}/workflows/{registryFlow}/instances/start?sync=false";

        try
        {
            var httpClient = httpClientFactory.CreateClient(HttpClientName);
            
            var response = await httpClient.PostAsJsonAsync(requestUrl, requestBody, JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogInformation(
                    "Domain registration completed successfully for domain '{DomainName}'. Response: {Response}",
                    domainName, responseContent);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogWarning(
                    "Domain registration failed for domain '{DomainName}'. Status: {StatusCode}, Error: {Error}",
                    domainName, response.StatusCode, errorContent);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Domain registration HTTP request failed for domain '{DomainName}'. Registry might be unavailable at '{RegistryUrl}'",
                domainName, requestUrl);
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            logger.LogWarning(ex,
                "Domain registration request timed out for domain '{DomainName}'",
                domainName);
        }
    }
}