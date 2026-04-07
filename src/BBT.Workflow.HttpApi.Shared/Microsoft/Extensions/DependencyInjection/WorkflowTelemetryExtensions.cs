using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Telemetry module: OpenTelemetry tracing and metrics via Aether SDK.
/// </summary>
public static class WorkflowTelemetryExtensions
{
    /// <summary>
    /// Registers Aether telemetry services (OpenTelemetry tracing/metrics).
    /// Used by all hosts for observability.
    /// </summary>
    public static IServiceCollection AddWorkflowTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAetherTelemetry(configuration);
        return services;
    }
}
