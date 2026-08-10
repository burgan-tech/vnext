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
public sealed record InstanceExecutionSnapshot(
    Guid Id,
    string? Key,
    InstanceStatus Status,
    string? CurrentState)
{
    /// <summary>True when the instance is currently Busy (a pipeline owns it).</summary>
    public bool IsBusy => Status.Equals(InstanceStatus.Busy);

    /// <summary>True when the instance reached a terminal Completed status.</summary>
    public bool IsCompleted => Status.Equals(InstanceStatus.Completed);
}
