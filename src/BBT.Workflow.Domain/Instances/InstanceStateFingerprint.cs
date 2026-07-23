namespace BBT.Workflow.Instances;

/// <summary>
/// Lightweight projection of the instance columns that determine whether a cached
/// state-function response is still valid. Loaded via a single-row projection query
/// (no includes) so long-poll validation does not materialize the full aggregate.
/// </summary>
/// <param name="Id">Identifier of the instance row actually resolved for the requested identifier.
/// Compared against the cached entry so a newer row reusing the same key invalidates the cache.</param>
/// <param name="Key">Instance business key.</param>
/// <param name="EffectiveState">Externally exposed state column (includes subflow propagation).</param>
/// <param name="Status">Instance status.</param>
/// <param name="FlowVersion">Bound flow version; a version migration can change transitions/views
/// without a state or status change.</param>
/// <param name="HasActiveSubFlow">True when an open SubFlow-type correlation exists. The state
/// response is then built from a live subflow call, so it must not be served from cache.</param>
public sealed record InstanceStateFingerprint(
    Guid Id,
    string? Key,
    string? EffectiveState,
    InstanceStatus Status,
    string? FlowVersion,
    bool HasActiveSubFlow)
{
    /// <summary>
    /// Builds the fingerprint from an already-loaded aggregate — the full-build path uses this
    /// so its ETag is computed from exactly the same values the projection query would return.
    /// </summary>
    public static InstanceStateFingerprint FromInstance(Instance instance) =>
        new(instance.Id,
            instance.Key,
            instance.EffectiveState,
            instance.Status,
            instance.FlowVersion,
            instance.HasActiveSubFlow);
}
