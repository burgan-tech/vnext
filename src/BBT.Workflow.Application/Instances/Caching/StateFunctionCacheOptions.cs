namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Configuration options for the state-function (long-poll) response cache.
/// The cache stores the full role-scoped state response together with a validation
/// fingerprint (effective state + status + flow version); each hit is validated
/// against the database with a single-row projection query instead of a full
/// aggregate load and response rebuild.
/// </summary>
public sealed class StateFunctionCacheOptions
{
    /// <summary>
    /// Configuration section name for state-function cache options.
    /// </summary>
    public const string SectionName = "StateFunctionCache";

    /// <summary>
    /// Gets or sets whether the state-function response cache is enabled.
    /// Default is true. Disable to force full evaluation on every poll (kill switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the cache entry TTL in seconds. Default is 60 — the default
    /// client long-poll timeout. The TTL only bounds residual staleness of parts
    /// not covered by the fingerprint (e.g. entity data ETag); state/status changes
    /// are detected on every hit via the fingerprint validation query.
    /// </summary>
    public int TtlSeconds { get; set; } = 60;

    /// <summary>
    /// Short freshness bound for active-SubFlow responses whose displayed state is owned by a
    /// child runtime and cannot be validated from the parent's local fingerprint.
    /// </summary>
    public int ActiveSubflowTtlMilliseconds { get; set; } = 500;
}
