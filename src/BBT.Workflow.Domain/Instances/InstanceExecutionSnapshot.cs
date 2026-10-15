namespace BBT.Workflow.Instances;

/// <summary>
/// Lightweight projection of the instance columns that drive transition admission:
/// status, chain ownership and current state. Loaded via a single-row projection query
/// (no includes) so the Busy pre-check and the reserve re-check under the status lock
/// never materialize the full aggregate.
/// </summary>
/// <param name="Id">Identifier of the instance row resolved for the requested identifier.</param>
/// <param name="Key">Instance business key.</param>
/// <param name="Status">Instance status at read time.</param>
/// <param name="CurrentState">Current state key of the instance.</param>
/// <param name="Flow">Bound workflow key — lets intake callers resolve the workflow definition
/// from the component cache without loading the aggregate.</param>
/// <param name="FlowVersion">Bound workflow version.</param>
/// <param name="HasActiveSubFlow">True when an open SubFlow-type correlation exists. A Busy
/// instance with an active SubFlow is not rejected with 409 at intake: a forwardable request is
/// proxied to the subflow (<c>SubflowProxyService</c>), and a non-forwardable one (updateData,
/// cancel/exit, a shared transition available here, an old-version chain-reserve relay) runs on the
/// instance, where the pipeline decides.</param>
/// <param name="ActiveSubFlow">The open blocking SubFlow (<c>S</c>) correlation's child, projected in
/// the same query (the filter <c>Instance.Subflow</c> uses). A parent holding one is a proxy for
/// forwardable transitions (<c>SubflowProxyService</c>). Null when none is open — a <c>P</c>
/// SubProcess or a completed correlation never counts.</param>
/// <param name="EffectiveStatus">The raw <c>EffectiveStatus</c> column at read time (the deepest
/// active SubFlow's status, else the row's own) — what an async proxy restores when its pre-stamped
/// Busy has to be undone.</param>
public sealed record InstanceExecutionSnapshot(
    Guid Id,
    string? Key,
    InstanceStatus Status,
    string? CurrentState,
    string? Flow,
    string? FlowVersion,
    bool HasActiveSubFlow,
    ActiveSubFlowRef? ActiveSubFlow = null,
    InstanceStatus? EffectiveStatus = null)
{
    /// <summary>True when the instance is currently Busy (a pipeline owns it).</summary>
    public bool IsBusy => Status.Equals(InstanceStatus.Busy);

    /// <summary>True when the instance reached a terminal Completed status.</summary>
    public bool IsCompleted => Status.Equals(InstanceStatus.Completed);

    /// <summary>
    /// True when the instance can no longer accept a transition. Mirrors
    /// <c>Instance.IsCompleted</c>, which counts Faulted and Passive as terminal too — a caller
    /// deciding admission from this projection instead of the aggregate must use this, not
    /// <see cref="IsCompleted"/>, or it would admit a faulted instance.
    /// </summary>
    public bool IsTerminal =>
        Status.Equals(InstanceStatus.Completed)
        || Status.Equals(InstanceStatus.Faulted)
        || Status.Equals(InstanceStatus.Passive);
}

/// <summary>
/// Identity of the active blocking SubFlow child of an instance, as carried on its open
/// <c>S</c> correlation: enough to route a transition to it (same-domain in process, cross-domain
/// through the internal relay endpoint) without loading the parent aggregate.
/// </summary>
/// <param name="InstanceId">The child SubFlow instance id.</param>
/// <param name="Domain">The child's domain.</param>
/// <param name="Flow">The child's workflow key.</param>
/// <param name="Version">The child's workflow version, when recorded.</param>
public sealed record ActiveSubFlowRef(Guid InstanceId, string Domain, string Flow, string? Version);
