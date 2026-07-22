namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Host-level configuration for the built-in instance function caches (data now; view,
/// schema, master in later phases). The effective TTL per workflow is author-controlled via
/// the flow definition's <c>functionCache.ttlSeconds</c>; this section only supplies the
/// default used when the workflow does not specify one. The state function has its own
/// section (<see cref="StateFunctionCacheOptions"/>) and is not covered here.
/// </summary>
public sealed class InstanceFunctionCacheOptions
{
    /// <summary>
    /// Configuration section name for instance function cache options.
    /// </summary>
    public const string SectionName = "InstanceFunctionCache";

    /// <summary>
    /// Gets or sets whether the built-in instance function caches are enabled.
    /// Default is true. Disable to force full evaluation on every request (kill switch).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the default cache entry TTL in seconds, used when the workflow
    /// definition does not declare <c>functionCache.ttlSeconds</c>. Default is 60.
    /// </summary>
    public int DefaultTtlSeconds { get; set; } = 60;
}
