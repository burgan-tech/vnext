namespace BBT.Workflow.Instances;

/// <summary>
/// Output from a retry operation on a faulted workflow instance.
/// </summary>
public sealed class RetryInstanceOutput
{
    /// <summary>
    /// The workflow instance identifier.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    /// Instance status after retry (Active, Busy, Completed, Faulted, etc.)
    /// </summary>
    public InstanceStatus? Status { get; init; }

    /// <summary>
    /// The ID of the transition that was retried.
    /// </summary>
    public Guid RetriedTransitionId { get; init; }
}
