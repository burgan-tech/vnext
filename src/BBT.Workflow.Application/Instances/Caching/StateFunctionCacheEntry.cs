namespace BBT.Workflow.Instances.Caching;

/// <summary>
/// Distributed-cache envelope for a state-function response. Stores the fully built,
/// caller-scoped <see cref="GetInstanceStateOutput"/> together with the fingerprint ETag it
/// was built under. Validation on a hit is a single ETag equality check: the current ETag is
/// recomputed from the lightweight fingerprint projection, so a matching entry is guaranteed
/// to reflect the instance's current effective state, status and flow version.
/// </summary>
public sealed class StateFunctionCacheEntry
{
    /// <summary>
    /// Unquoted fingerprint ETag the response was built under
    /// (hash of instance id + effective state + status + flow version + caller scope).
    /// </summary>
    public string Etag { get; set; } = string.Empty;

    /// <summary>ETag derived only from the local parent fingerprint.</summary>
    public string ParentEtag { get; set; } = string.Empty;

    public bool IsActiveSubflowSnapshot { get; set; }

    /// <summary>
    /// Entity (instance data) ETag at build time. May lag behind data-only updates until the
    /// next state/status change — accepted: the state function tracks state/status, not data.
    /// </summary>
    public string EntityEtag { get; set; } = string.Empty;

    /// <summary>
    /// The fully built state-function response for the caller scope encoded in the cache key.
    /// </summary>
    public GetInstanceStateOutput Output { get; set; } = new();
}
