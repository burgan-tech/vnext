using System;
using System.Net.Http.Json;
using System.Text.Json;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
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
    IHostEnvironment hostEnvironment,
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

        // Check if service discovery is enabled
        if (!options.Enabled)
        {
            logger.LogDebug("Service discovery is disabled. Skipping domain registration");
            return;
        }

        logger.LogInformation("Service discovery is enabled. Starting domain registration...");

        var vNextApiBaseUrl = configuration[VNextApiBaseUrlKey];

        if (string.IsNullOrWhiteSpace(vNextApiBaseUrl))
        {
            throw new InvalidConfigurationException(
                $"Service discovery is enabled, but '{VNextApiBaseUrlKey}' is not configured. " +
                "Either disable service discovery or configure the base URL.");
        }

        if (IsLocalhostBaseUrl(vNextApiBaseUrl) && !hostEnvironment.IsDevelopment())
        {
            throw new InvalidConfigurationException(
                $"Invalid configuration: '{VNextApiBaseUrlKey}' points to localhost ('{vNextApiBaseUrl}') " +
                $"in environment '{hostEnvironment.EnvironmentName}'. " +
                "Use a reachable base URL in non-development environments.");
        }

        var domainName = runtimeInfoProvider.Domain;
        var baseUrl = vNextApiBaseUrl.TrimEnd('/');
        var healthUrl = $"{baseUrl}/health";
        var appId = configuration["DAPR_APP_ID"];
        
        logger.LogInformation(
            "Registering domain '{DomainName}' with baseUrl '{BaseUrl}' and healthUrl '{HealthUrl}' to registry '{RegistryUrl}'",
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
                var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
                logger.LogInformation(
                    "Domain registration completed successfully for domain '{DomainName}'. Response: {Response}",
                    domainName, responseContent);
            }
            else
            {
                var errorContent = await response.ReadDecompressedContentAsync(cancellationToken);
                var reason = $"HTTP {(int)response.StatusCode} - {errorContent}";
                
                throw new DomainRegistrationFailedException(domainName, requestUrl, reason);
            }
        }
        catch (DomainRegistrationFailedException)
        {
            // Re-throw domain registration exceptions as-is
            throw;
        }
        catch (HttpRequestException ex)
        {
            var reason = $"HTTP request failed: {ex.Message}. Registry might be unavailable.";
            throw new DomainRegistrationFailedException(domainName, requestUrl, reason);
        }
        catch (TaskCanceledException ex) when (ex.CancellationToken != cancellationToken)
        {
            var reason = "Request timed out. Registry might be slow or unavailable.";
            throw new DomainRegistrationFailedException(domainName, requestUrl, reason);
        }
    }

    private static bool IsLocalhostBaseUrl(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return IsLocalhostHost(uri.Host);
        }

        return ContainsLocalhostToken(baseUrl);
    }

    private static bool IsLocalhostHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsLocalhostToken(string baseUrl)
        => baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
           || baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || baseUrl.Contains("::1", StringComparison.OrdinalIgnoreCase);
}