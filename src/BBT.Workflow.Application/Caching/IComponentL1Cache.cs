namespace BBT.Workflow.Caching;

/// <summary>
/// In-process (L1) cache for component cache envelopes, sitting in front of the distributed (L2)
/// cache. Keys are the L2 cache keys, so version-resolution entries inherit their generation scoping
/// — a publish bump changes the key and stale entries simply stop being reachable.
/// </summary>
/// <remarks>
/// Stores envelopes as serialized bytes and deserializes per read, so every hit returns a fresh
/// instance — the same isolation callers get from an L2 read today. Negative envelopes are never
/// stored. All members are optimizations: they must never throw into the read path.
/// </remarks>
public interface IComponentL1Cache : IDisposable
{
    /// <summary>Returns the cached envelope for the key, or null on miss/disabled.</summary>
    CacheEnvelope<T>? TryGet<T>(string cacheKey) where T : class;

    /// <summary>Stores the envelope until the given expiry. Negative envelopes are ignored.</summary>
    void Set<T>(string cacheKey, CacheEnvelope<T> envelope, DateTimeOffset absoluteExpiration) where T : class;

    /// <summary>Removes the entry if present.</summary>
    void Remove(string cacheKey);
}
