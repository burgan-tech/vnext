using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.Monitor.HealthChecks;

/// <summary>
/// Health check that verifies the distributed cache (Redis via Dapr) is reachable
/// by performing a lightweight set/get round-trip.
/// </summary>
public sealed class MonitorDistributedCacheHealthCheck(IDistributedCache distributedCache) : IHealthCheck
{
    private const string ProbeKey = "monitor:health:probe";

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var value = "ok"u8.ToArray();
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10)
            };

            await distributedCache.SetAsync(ProbeKey, value, options, cancellationToken);
            var result = await distributedCache.GetAsync(ProbeKey, cancellationToken);

            if (result is null || result.Length == 0)
                return HealthCheckResult.Degraded("Distributed cache returned empty response for probe key.");

            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Distributed cache is unreachable.", ex);
        }
    }
}
