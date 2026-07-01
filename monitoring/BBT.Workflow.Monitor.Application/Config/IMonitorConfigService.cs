using BBT.Aether.Results;
using BBT.Workflow.Monitor.Config.DTOs;

namespace BBT.Workflow.Monitor.Config;

/// <summary>Read-only runtime/monitor configuration (P9). Secrets excluded.</summary>
public interface IMonitorConfigService
{
    /// <summary>Returns runtime and monitor configuration. Connection strings and tokens are never included.</summary>
    Task<Result<MonitorConfigResponse>> GetConfigAsync(CancellationToken cancellationToken = default);
}
