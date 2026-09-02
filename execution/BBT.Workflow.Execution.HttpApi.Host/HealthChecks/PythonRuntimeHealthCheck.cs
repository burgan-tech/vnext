using BBT.Workflow.Execution.Python;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BBT.Workflow.HealthChecks;

public sealed class PythonRuntimeHealthCheck(IPythonRuntimeRegistry registry) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await registry.CheckEnabledRuntimesAsync(cancellationToken);
            return HealthCheckResult.Healthy("Enabled Python runtimes are available.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("A configured Python runtime is unavailable.", ex);
        }
    }
}
