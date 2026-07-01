namespace BBT.Workflow.Monitor.Config.DTOs;

/// <summary>Runtime and monitor configuration (P9). Secrets and connection strings excluded.</summary>
public sealed class MonitorConfigResponse
{
    /// <summary>Assembly version of the monitor host.</summary>
    public string? RuntimeVersion { get; set; }

    /// <summary>Non-secret monitor toggles and modes.</summary>
    public MonitorRuntimeFlags Monitor { get; set; } = new();
}

/// <summary>Non-secret runtime flags.</summary>
public sealed class MonitorRuntimeFlags
{
    /// <summary>Redis connection mode (e.g. "Standalone", "Cluster").</summary>
    public string? RedisMode { get; set; }

    /// <summary>Whether OpenTelemetry tracing is enabled.</summary>
    public bool TracingEnabled { get; set; }

    /// <summary>Whether OpenTelemetry metrics are enabled.</summary>
    public bool MetricsEnabled { get; set; }

    /// <summary>Whether Dapr Vault secret store is enabled.</summary>
    public bool VaultEnabled { get; set; }
}
