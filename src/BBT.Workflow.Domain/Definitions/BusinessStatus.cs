namespace BBT.Workflow.Definitions;

/// <summary>
/// Represents the business-level outcome of a task execution.
/// Separate from TaskStatus which tracks platform/infrastructure status.
/// </summary>
/// <remarks>
/// - TaskStatus.Completed + BusinessStatus.Success = Task executed and business logic succeeded
/// - TaskStatus.Completed + BusinessStatus.Failed = Task executed but business logic failed (e.g., HTTP 4xx/5xx)
/// - TaskStatus.Faulted + BusinessStatus.Unknown = Task could not be executed (infrastructure error)
/// </remarks>
public enum BusinessStatus
{
    /// <summary>
    /// Business status is not yet determined or not applicable.
    /// Used when task is still executing or when infrastructure error occurred.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Business logic executed successfully.
    /// StandardTaskResponse.IsSuccess was true.
    /// </summary>
    Success = 1,

    /// <summary>
    /// Business logic failed.
    /// StandardTaskResponse.IsSuccess was false (e.g., HTTP 4xx/5xx status codes).
    /// </summary>
    Failed = 2
}

