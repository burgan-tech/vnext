namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>Database health check caching configuration.</summary>
public sealed class HealthCheckCacheOptions
{
    public const string SectionName = "HealthChecks:Database";

    /// <summary>How long (in seconds) the last DB probe result is reused. Default: 60.</summary>
    public int CacheTtlSeconds { get; set; } = 60;

    /// <summary>Convenience accessor returning CacheTtlSeconds as a TimeSpan.</summary>
    public TimeSpan Ttl => TimeSpan.FromSeconds(CacheTtlSeconds);
}
