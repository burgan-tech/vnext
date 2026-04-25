namespace BBT.Workflow.Caching;

/// <summary>
/// Concurrency limits for cache warm-up operations.
/// Conservative defaults are chosen to keep live request traffic unaffected
/// (no Redis multiplexer back-pressure, no DB connection-pool exhaustion).
/// Total simultaneous Redis/DB calls = MaxConcurrencyPerCacheSet * MaxConcurrencyAcrossCacheSets.
/// </summary>
public sealed class CacheWarmupOptions
{
    public const string SectionName = "CacheWarmup";

    /// <summary>
    /// Max parallel cache-key fetches inside a single CacheSet during warm-up.
    /// </summary>
    public int MaxConcurrencyPerCacheSet { get; set; } = 8;

    /// <summary>
    /// Max CacheSets warmed in parallel inside DomainCacheContext.
    /// </summary>
    public int MaxConcurrencyAcrossCacheSets { get; set; } = 2;
}
