using BBT.Workflow.Instances.DTOs;

namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed cache and fingerprint-ETag calculator serving BOTH the master and the schema
/// instance functions (they share the <see cref="GetSchemaOutput"/> body shape). Keys and ETags
/// are scoped to the instance identifier and the caller context (roles, actor, culture,
/// requested workflow version); the schema function additionally folds the transition key and
/// the effective state into its scope — transition resolution is state-dependent. The change
/// signal is the latest instance-data ETag plus the bound flow version, mirroring the data
/// function. Extensions play no role in these functions. Implementations must never let a
/// cache failure fail the request: read errors are reported as a miss, write errors are
/// swallowed.
/// </summary>
public interface IInstanceSchemaFunctionCache
{
    /// <summary>
    /// Whether the cache is enabled (bound from the <c>InstanceFunctionCache</c> configuration
    /// section, shared with the data function). The ETag computation is pure and usable
    /// regardless of this flag.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Builds the master-function cache key:
    /// <c>master-fn:{domain}:{workflow}:{instance}:{callerHash}</c>.
    /// </summary>
    string BuildKey(GetMasterInput input);

    /// <summary>
    /// Builds the schema-function cache key:
    /// <c>schema-fn:{domain}:{workflow}:{instance}:{callerHash}:{transitionKey}</c>.
    /// </summary>
    string BuildKey(GetSchemaInput input, string transitionKey);

    /// <summary>
    /// Master ETag: hash of (instance id, latest data ETag, flow version, caller scope) —
    /// the flow-level master schema is state-independent.
    /// </summary>
    string ComputeEtag(GetMasterInput input, InstanceDataFingerprint fingerprint);

    /// <summary>
    /// Schema ETag: hash of (instance id, latest data ETag, effective state, flow version,
    /// caller scope, transition key) — transition resolution depends on the current state
    /// (equal to the effective state whenever no active subflow exists, and subflow-active
    /// instances bypass this cache).
    /// </summary>
    string ComputeEtag(GetSchemaInput input, InstanceDataFingerprint fingerprint, string transitionKey);

    /// <summary>
    /// Resolves the effective TTL: the workflow author's <c>functionCache.ttlSeconds</c> when
    /// positive, otherwise the host default (<c>InstanceFunctionCache:DefaultTtlSeconds</c>).
    /// </summary>
    int ResolveTtlSeconds(Definitions.FunctionCacheDefinition? functionCache);

    /// <summary>
    /// Gets the cached entry for the key, or null on miss or cache failure.
    /// </summary>
    Task<SchemaFunctionCacheEntry?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores the entry under the key with the given TTL. Failures are swallowed.
    /// </summary>
    Task SetAsync(string key, SchemaFunctionCacheEntry entry, int ttlSeconds, CancellationToken cancellationToken = default);
}
