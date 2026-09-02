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
/// instance with an active SubFlow must NOT be rejected with 409 — the pipeline forwards the
/// request to the subflow instead (<c>ForwardToActiveSubflowStep</c>).</param>
public sealed record InstanceExecutionSnapshot(
    Guid Id,
    string? Key,
    InstanceStatus Status,
    string? CurrentState,
    string? Flow,
    string? FlowVersion,
    bool HasActiveSubFlow)
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
