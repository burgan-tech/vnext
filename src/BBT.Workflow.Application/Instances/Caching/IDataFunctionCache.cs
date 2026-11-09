namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed cache and fingerprint-ETag calculator for data-function responses. Keys and
/// ETags are scoped to the instance identifier and the caller context (roles, actor, culture,
/// requested data version) because the response is authorization-filtered (x-roles).
/// Extensions are deliberately OUTSIDE the scope: the ETag tracks the data change point only,
/// the cache stores pure instance data (a validated entry feeds the build path's Data portion
/// regardless of whether extensions were requested), and extension output is always computed
/// fresh. Only latest-data requests use the cache — a pinned-version body can change without
/// moving the latest row's ETag (older-line writes). Implementations must never let a cache
/// failure fail the request: read errors are reported as a miss, write errors are swallowed.
/// </summary>
public interface IDataFunctionCache
{
    /// <summary>
    /// Whether the cache is enabled (bound from the <c>InstanceFunctionCache</c> configuration
    /// section). The ETag computation is pure and usable regardless of this flag.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Builds the cache key for the given request:
    /// <c>data-fn:{domain}:{workflow}:{instance}:{callerHash}</c>.
    /// </summary>
    string BuildKey(GetInstanceDataInput input);

    /// <summary>
    /// Computes the deterministic REPRESENTATION ETag — what the caller receives and what an
    /// If-None-Match is matched against: hash of (instance id, latest data ETag, effective state,
    /// flow version, caller scope). Computable from the lightweight fingerprint projection alone,
    /// so a match can be answered with 304 without loading the aggregate, running extensions, or
    /// building the response. For pinned-version requests the caller passes a fingerprint carrying
    /// the resolved row's ETag instead of the latest one.
    /// </summary>
    string ComputeEtag(GetInstanceDataInput input, InstanceDataFingerprint fingerprint);

    /// <summary>
    /// Computes the CACHE-VALIDITY ETag — the identity of the cached <b>data payload</b>, which is
    /// all an entry holds: hash of (instance id, latest data ETag, flow version, caller scope),
    /// deliberately WITHOUT the effective state.
    /// <para>
    /// The two are separate because they answer different questions. "May I answer 304?" depends on
    /// the state as well, since extension output is derived from it. "Is the stored body still the
    /// right body?" does not: a transition that writes no data leaves the data payload — and its
    /// x-roles field filtering — exactly as it was. Folding state into this identity would discard a
    /// still-correct body on every such transition (auto-transition hops included) and pay for the
    /// filtering pass again, for nothing.
    /// </para>
    /// </summary>
    string ComputeDataEtag(GetInstanceDataInput input, InstanceDataFingerprint fingerprint);

    /// <summary>
    /// Resolves the effective TTL: the workflow author's <c>functionCache.ttlSeconds</c> when
    /// positive, otherwise the host default (<c>InstanceFunctionCache:DefaultTtlSeconds</c>).
    /// </summary>
    int ResolveTtlSeconds(Definitions.FunctionCacheDefinition? functionCache);

    /// <summary>
    /// Gets the cached entry for the key, or null on miss or cache failure.
    /// </summary>
    Task<DataFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the entry under the key with the given TTL — resolved by the caller from the
    /// workflow definition (<c>functionCache.ttlSeconds</c>) falling back to the configured
    /// default. Failures are swallowed.
    /// </summary>
    Task SetAsync(string key, DataFunctionCacheEntry entry, int ttlSeconds, CancellationToken cancellationToken = default);
}
