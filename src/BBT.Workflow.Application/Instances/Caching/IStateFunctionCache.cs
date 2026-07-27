namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed cache and fingerprint-ETag calculator for state-function (long-poll) responses.
/// Keys and ETags are scoped to the instance identifier and the caller context (roles, actor,
/// culture, extensions, version) because the response content is authorization- and
/// localization-dependent. Implementations must never let a cache failure fail the request:
/// read errors are reported as a miss, write errors are swallowed.
/// </summary>
public interface IStateFunctionCache
{
    /// <summary>
    /// Whether the cache is enabled (bound from the <c>StateFunctionCache</c> configuration section).
    /// The ETag computation methods are pure and usable regardless of this flag.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Builds the cache key for the given request:
    /// <c>state-fn:{domain}:{workflow}:{instance}:{callerHash}</c>. The caller hash covers
    /// role/roles, the current actor identity ($InstanceStarter/$PreviousUser pseudo-roles are
    /// actor-dependent), the resolved culture (state alias labels are localized), requested
    /// extensions and workflow version. A client alternating between id and key for the same
    /// instance produces two independent entries — accepted, TTL-bounded.
    /// </summary>
    string BuildKey(GetInstanceStateInput input);

    /// <summary>
    /// Computes the deterministic fingerprint ETag for a subflow-free instance:
    /// hash of (instance id, effective state, status, flow version, caller scope).
    /// Computable from the lightweight fingerprint projection alone, so an If-None-Match
    /// match can be answered with 304 without loading the aggregate or building the response.
    /// The caller scope is part of the hash so a role/culture switch never yields a false 304.
    /// </summary>
    string ComputeEtag(GetInstanceStateInput input, InstanceStateFingerprint fingerprint);

    /// <summary>
    /// Computes the fingerprint ETag for an instance with an active SubFlow. The displayed
    /// state and status come from the live subflow response and are folded into the hash —
    /// the parent row's fingerprint alone cannot see subflow-internal Busy/Active flips.
    /// </summary>
    string ComputeEtag(GetInstanceStateInput input, InstanceStateFingerprint fingerprint, GetInstanceStateOutput subFlowOutput);

    /// <summary>
    /// Gets the cached entry for the key, or null on miss or cache failure.
    /// </summary>
    Task<StateFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the entry under the key with the configured TTL. Failures are swallowed.
    /// </summary>
    Task SetAsync(string key, StateFunctionCacheEntry entry, CancellationToken cancellationToken = default);
}
