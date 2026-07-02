using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Monitor.Config.DTOs;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Monitor.Config;

/// <inheritdoc cref="IMonitorConfigService" />
public sealed class MonitorConfigService(
    IServiceProvider serviceProvider,
    IConfiguration configuration)
    : ApplicationService(serviceProvider), IMonitorConfigService
{
    /// <inheritdoc />
    public Task<Result<MonitorConfigResponse>> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var response = new MonitorConfigResponse
        {
            RuntimeVersion = typeof(MonitorConfigService).Assembly.GetName().Version?.ToString(),
            Monitor = new MonitorRuntimeFlags
            {
                RedisMode      = configuration["Redis:Mode"],
                TracingEnabled = configuration.GetValue<bool>("Telemetry:TracingEnabled"),
                MetricsEnabled = configuration.GetValue<bool>("Telemetry:MetricsEnabled"),
                VaultEnabled   = configuration.GetValue<bool>("Vault:Enabled")
            }
        };
        return Task.FromResult(Result<MonitorConfigResponse>.Ok(response));
    }
}
