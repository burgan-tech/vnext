using BBT.Aether.AspNetCore.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>
/// Exposes a detailed health check report for operator tooling.
/// Standard liveness and readiness probes are served by
/// <c>/live</c> and <c>/ready</c> (combined report at <c>/health</c>).
/// </summary>
[ApiController]
[Route("monitor/health")]
public sealed class MonitorHealthController(HealthCheckService healthCheckService) : AetherControllerBase
{
    /// <summary>
    /// Returns a per-component health report for all registered health checks.
    /// Includes status, duration, and exception details for each operator
    /// (PostgreSQL, Redis, vNext Orchestrator, self).
    /// </summary>
    /// <response code="200">System is fully healthy</response>
    /// <response code="503">One or more components are degraded or unhealthy</response>
    [HttpGet("detail")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetDetailAsync(CancellationToken cancellationToken = default)
    {
        var report = await healthCheckService.CheckHealthAsync(cancellationToken);

        var detail = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                durationMs = e.Value.Duration.TotalMilliseconds,
                description = e.Value.Description,
                exception = e.Value.Exception?.Message,
                data = e.Value.Data
            })
        };

        return report.Status == HealthStatus.Healthy
            ? Ok(detail)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, detail);
    }
}
