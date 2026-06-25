using Microsoft.Extensions.Diagnostics.HealthChecks;
using Prometheus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extensions for configuring health checks in Workflow API applications.
/// Registers only the lightweight "self" liveness check.
/// Database readiness check is added separately by hosts that own a DB connection (Orchestration).
/// </summary>
public static class HealthChecksServiceCollectionExtensions
{
    /// <summary>
    /// Adds the base health checks shared by all Workflow application hosts:
    /// a "self" liveness check (tagged "live") and Prometheus forwarding.
    /// Does NOT include a database check — add that per-host where needed.
    /// </summary>
    public static IServiceCollection AddAppHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .ForwardToPrometheus()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        return services;
    }
} 