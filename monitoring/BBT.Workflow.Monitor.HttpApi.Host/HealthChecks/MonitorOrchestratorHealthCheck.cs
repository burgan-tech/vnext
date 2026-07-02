using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.Monitor.HealthChecks;

/// <summary>
/// Health check that verifies the vNext Orchestration API is reachable by probing its
/// <c>/live</c> endpoint, matching orchestration host health mapping (<c>/health</c>, <c>/live</c>, <c>/ready</c>).
/// The base URL is resolved from configuration key
/// <c>vNextApi:BaseUrl</c> (same key used by the Orchestration service).
/// </summary>
public sealed class MonitorOrchestratorHealthCheck(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["vNextApi:BaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            return HealthCheckResult.Degraded("Orchestrator base URL is not configured (vNextApi:BaseUrl).");

        try
        {
            var client = httpClientFactory.CreateClient("orchestrator-health");
            var probe = $"{baseUrl.TrimEnd('/')}/live";
            var response = await client.GetAsync(probe, cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded(
                    $"Orchestrator responded with HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Orchestrator is unreachable.", ex);
        }
    }
}
