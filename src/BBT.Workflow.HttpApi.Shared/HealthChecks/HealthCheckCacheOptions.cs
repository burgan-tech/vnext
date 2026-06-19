namespace BBT.Workflow.HttpApi.Shared.HealthChecks;

/// <summary>
/// Configuration options for <see cref="CachedHealthCheck"/>.
/// Consumed at DI registration time to supply the TTL passed to the
/// <see cref="CachedHealthCheck"/> constructor. Bind via
/// <c>IOptions&lt;HealthCheckCacheOptions&gt;</c> in the registration extension.
/// </summary>
public sealed class HealthCheckCacheOptions
{
    /// <summary>
    /// How long a health check result is cached before the inner check is re-evaluated.
    /// Defaults to 10 seconds.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(10);
}
