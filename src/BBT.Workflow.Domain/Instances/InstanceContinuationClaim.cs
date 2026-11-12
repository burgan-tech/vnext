namespace BBT.Workflow.Instances;

/// <summary>
/// The continuation claim on a parked Busy instance (<see cref="IInstanceRepository.TryClaimContinuationAsync"/>).
/// It lives in a shadow column of <c>Instances</c>: nothing in the domain reads it, only the
/// set-based claim and its release, so it is not a property of the aggregate.
/// </summary>
public static class InstanceContinuationClaim
{
    /// <summary>Shadow column holding the claim timestamp (UTC); null when unclaimed.</summary>
    public const string ColumnName = "ContinuationClaimedAt";

    /// <summary>
    /// A claim older than this is treated as abandoned. Generous against the measured chain
    /// durations (seconds), short enough that a crashed owner does not close a fan-in gate for long.
    /// </summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(60);
}
