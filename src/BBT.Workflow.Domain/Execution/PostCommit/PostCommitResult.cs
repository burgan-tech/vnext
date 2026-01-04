using BBT.Aether.Results;

namespace BBT.Workflow.Execution.PostCommit;

/// <summary>
/// Request to mark the workflow instance as faulted.
/// Includes error details for tracking.
/// </summary>
/// <param name="ErrorCode">The error code that caused the fault.</param>
/// <param name="ErrorMessage">The error message describing the fault.</param>
public sealed record PostCommitFaultRequest(string ErrorCode, string? ErrorMessage);

/// <summary>
/// Result of post-commit job execution.
/// Extends beyond simple success/failure to include fault request for instance state management.
/// </summary>
public sealed class PostCommitResult
{
    /// <summary>
    /// Gets a value indicating whether all jobs executed successfully.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Gets the error that occurred, if any.
    /// </summary>
    public Error? Error { get; init; }

    /// <summary>
    /// Gets the fault request if the instance should be marked as faulted.
    /// When present, the pipeline should reacquire the lock and update instance status.
    /// </summary>
    public PostCommitFaultRequest? FaultRequest { get; init; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static PostCommitResult Ok() => new() { IsSuccess = true };

    /// <summary>
    /// Creates a failed result without fault request.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    public static PostCommitResult Fail(Error error)
        => new() { IsSuccess = false, Error = error };

    /// <summary>
    /// Creates a failed result with optional fault request.
    /// </summary>
    /// <param name="error">The error that occurred.</param>
    /// <param name="faultRequest">Optional request to mark instance as faulted.</param>
    public static PostCommitResult Fail(Error error, PostCommitFaultRequest? faultRequest)
        => new() { IsSuccess = false, Error = error, FaultRequest = faultRequest };
}

