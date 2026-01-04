using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Context provided to the failure policy for making decisions.
/// </summary>
/// <param name="Job">The job that failed.</param>
/// <param name="Error">The error that occurred.</param>
/// <param name="JobIndex">Zero-based index of the failed job in the batch.</param>
/// <param name="TotalJobs">Total number of jobs in the batch.</param>
public sealed record PostCommitFailureContext(
    IPostCommitJob Job,
    Error Error,
    int JobIndex,
    int TotalJobs);

/// <summary>
/// Decision returned by the failure policy.
/// </summary>
/// <param name="ShouldContinue">Whether to continue processing remaining jobs.</param>
/// <param name="ShouldMarkInstanceFaulted">Whether the workflow instance should be marked as faulted.</param>
/// <param name="FaultErrorCode">Error code to use when marking the instance as faulted.</param>
/// <param name="FaultErrorMessage">Error message to use when marking the instance as faulted.</param>
public sealed record PostCommitFailureDecision(
    bool ShouldContinue,
    bool ShouldMarkInstanceFaulted,
    string FaultErrorCode,
    string? FaultErrorMessage);

/// <summary>
/// Policy interface for determining how to handle post-commit job failures.
/// Implementations can customize behavior based on job type, error type, etc.
/// </summary>
public interface IPostCommitFailurePolicy
{
    /// <summary>
    /// Decides how to handle a post-commit job failure.
    /// </summary>
    /// <param name="context">Context containing failure details.</param>
    /// <returns>Decision on how to proceed.</returns>
    PostCommitFailureDecision Decide(PostCommitFailureContext context);
}

