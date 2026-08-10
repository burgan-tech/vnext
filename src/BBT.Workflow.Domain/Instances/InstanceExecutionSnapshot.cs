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
/// <param name="ChainToken">Durable chain ownership token, or null when no chain owns the instance.
/// Compared against the executing chain's token to re-assert ownership between hops and before
/// status writes — a rotated token means a cancel/exit (or the reaper) took over the instance.</param>
/// <param name="CurrentState">Current state key of the instance.</param>
public sealed record InstanceExecutionSnapshot(
    Guid Id,
    string? Key,
    InstanceStatus Status,
    Guid? ChainToken,
    string? CurrentState)
{
    /// <summary>True when the instance is currently Busy (a pipeline owns it).</summary>
    public bool IsBusy => Status.Equals(InstanceStatus.Busy);

    /// <summary>True when the instance reached a terminal Completed status.</summary>
    public bool IsCompleted => Status.Equals(InstanceStatus.Completed);

    /// <summary>
    /// Returns whether the supplied token matches the snapshot's chain ownership token.
    /// Mirrors <see cref="Instance.MatchesChain"/> for detached admission checks.
    /// </summary>
    public bool MatchesChain(Guid token) => ChainToken.HasValue && ChainToken.Value == token;
}
