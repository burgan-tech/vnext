using BBT.Workflow.HttpApi.Shared.HealthChecks;
using HealthChecks.NpgSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Prometheus;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extensions for configuring health checks in Workflow API applications.
/// </summary>
public static class HealthChecksServiceCollectionExtensions
{
    /// <summary>
    /// Adds standard health checks for Workflow API applications.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="includeDatabaseCheck">
    /// When <see langword="true"/> (default), registers a <c>"database"</c> readiness check that
    /// connects to the <c>"Default"</c> PostgreSQL connection string. The check is wrapped in a
    /// <see cref="CachedHealthCheck"/> so the underlying connection is opened at most once per TTL
    /// (configurable via <see cref="HealthCheckCacheOptions"/>; default 10 s) and is subject to a
    /// hard per-invocation timeout of 2 seconds. Set to <see langword="false"/> for hosts (e.g.
    /// background workers) that should not contribute a database readiness gate.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAppHealthChecks(
        this IServiceCollection services,
        bool includeDatabaseCheck = true)
    {
        var healthChecksBuilder = services
            .AddHealthChecks()
            .ForwardToPrometheus();

        healthChecksBuilder
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);

        if (includeDatabaseCheck)
        {
            var configuration = services.GetConfiguration();
            var connectionString = configuration.GetConnectionString("Default")!;

            healthChecksBuilder.Add(new HealthCheckRegistration(
                name: "database",
                factory: sp =>
                {
                    var ttl = sp.GetService<IOptions<HealthCheckCacheOptions>>()?.Value.Ttl
                              ?? new HealthCheckCacheOptions().Ttl;

                    IHealthCheck inner = new NpgSqlHealthCheck(
                        new NpgSqlHealthCheckOptions(connectionString));

                    return new CachedHealthCheck(inner, ttl, TimeProvider.System);
                },
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"],
                timeout: TimeSpan.FromSeconds(2)));
        }

        return services;
    }
}
