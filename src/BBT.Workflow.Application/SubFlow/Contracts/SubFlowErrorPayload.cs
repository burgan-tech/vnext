namespace BBT.Workflow.SubFlow;

/// <summary>
/// Payload containing error information from a failed SubFlow.
/// Used to propagate errors from child workflows to parent workflows.
/// </summary>
public sealed record SubFlowErrorPayload
{
    /// <summary>
    /// The exception type name from the child workflow.
    /// </summary>
    public string ExceptionTypeName { get; init; } = string.Empty;

    /// <summary>
    /// The error message from the child workflow.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Optional error code from the child workflow.
    /// </summary>
    public int? ErrorCode { get; init; }

    /// <summary>
    /// Optional error details (may be sanitized for PII).
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Whether the error was a timeout.
    /// </summary>
    public bool IsTimeout { get; init; }

    /// <summary>
    /// The state where the error occurred in the child workflow.
    /// </summary>
    public string? ChildState { get; init; }

    /// <summary>
    /// The transition being executed when the error occurred.
    /// </summary>
    public string? ChildTransition { get; init; }

    /// <summary>
    /// The child instance ID where the error occurred.
    /// </summary>
    public Guid ChildInstanceId { get; init; }

    /// <summary>
    /// Timestamp when the error occurred.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates an empty error payload (no error).
    /// </summary>
    public static SubFlowErrorPayload Empty => new();
}

