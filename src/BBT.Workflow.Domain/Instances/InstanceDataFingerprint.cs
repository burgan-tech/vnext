namespace BBT.Workflow.Instances;

/// <summary>
/// Lightweight projection of the columns that determine whether a cached data-function
/// response is still valid: the latest data row's ETag (a new ULID on every latest-line
/// data write) plus the bound flow version (a migration can change x-roles field filtering
/// and extension definitions without a data write). Loaded via a single-row projection —
/// the latest ETag read is an index-only probe on <c>UX_InstancesData_Instance_IsLatest</c>.
/// </summary>
/// <param name="Id">Identifier of the instance row actually resolved for the requested identifier.
/// Compared inside the ETag hash so a newer row reusing the same key invalidates cached answers.</param>
/// <param name="Key">Instance business key.</param>
/// <param name="LatestDataEtag">ETag of the IsLatest instance-data row; null when the instance
/// has no data rows yet.</param>
/// <param name="FlowVersion">Bound flow version.</param>
/// <param name="EffectiveState">Externally exposed state column — the schema function's
/// transition resolution is state-dependent (equals CurrentState when no active subflow).</param>
/// <param name="HasActiveSubFlow">True when an open SubFlow-type correlation exists. Master and
/// schema responses are then composed from a live subflow call and must not be served from cache.</param>
public sealed record InstanceDataFingerprint(
    Guid Id,
    string? Key,
    string? LatestDataEtag,
    string? FlowVersion,
    string? EffectiveState,
    bool HasActiveSubFlow)
{
    /// <summary>
    /// Builds the fingerprint from an already-loaded aggregate — the full-build path uses this
    /// so its ETag is computed from exactly the same values the projection query would return.
    /// </summary>
    public static InstanceDataFingerprint FromInstance(Instance instance) =>
        new(instance.Id,
            instance.Key,
            instance.LatestData?.ETag,
            instance.FlowVersion,
            instance.EffectiveState,
            instance.HasActiveSubFlow);
}
