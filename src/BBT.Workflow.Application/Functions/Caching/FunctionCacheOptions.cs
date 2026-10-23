using System.ComponentModel.DataAnnotations;

namespace BBT.Workflow.Functions.Caching;

/// <summary>
/// Process-level tunables for the function read-through cache (<c>FunctionCache:*</c>). They change how
/// the runtime serves a function that already declares <c>attributes.cache</c>; they never enable caching
/// for a function that does not.
/// </summary>
public sealed class FunctionCacheOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "FunctionCache";

    /// <summary>
    /// Lifetime of the per-pod (L1) copy of a cached function response and of the generation stamp it was
    /// keyed under, in milliseconds. <c>0</c> (default) disables the tier: every request reads the shared
    /// state store, exactly as before. When enabled, a generation bump (or any state-store write) becomes
    /// visible to a pod at most this long after it happened — the tier's whole invalidation story, so keep
    /// it short (a few seconds at most).
    /// </summary>
    [Range(0, 60_000)]
    public int LocalTtlMilliseconds { get; set; }

    /// <summary>Upper bound of the L1 tier's memory, in megabytes (sized by serialized response bytes).</summary>
    [Range(1, 4096)]
    public int LocalSizeLimitMb { get; set; } = 64;

    /// <summary>
    /// When <c>true</c>, concurrent cache misses for the same key in one pod share a single execution
    /// instead of each running the function's tasks (miss stampede after a TTL expiry or a generation bump).
    /// Default <c>false</c>: opt-in per deployment, so a runtime upgrade changes no domain's behaviour.
    /// </summary>
    public bool SingleFlight { get; set; }

    /// <summary>
    /// When <c>true</c>, an instance-scoped function first resolves the instance header only; a cached
    /// function computes its key from it and loads the full instance (data, correlations) only on a miss.
    /// Falls back to the full load whenever the key could depend on the instance data. Default <c>false</c>:
    /// opt-in per deployment — with it off, every instance function loads exactly as before.
    /// </summary>
    public bool LeanInstanceKey { get; set; }
}
