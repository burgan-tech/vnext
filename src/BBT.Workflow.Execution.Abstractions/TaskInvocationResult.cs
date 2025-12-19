namespace BBT.Workflow.Execution;

/// <summary>
/// Result from task invocation - completely independent of Domain types.
/// </summary>
public sealed class TaskInvocationResult
{
    /// <summary>
    /// Indicates whether the task execution was successful.
    /// </summary>
    public bool IsSuccess { get; init; }
    
    /// <summary>
    /// HTTP status code if applicable.
    /// </summary>
    public int? StatusCode { get; init; }
    
    /// <summary>
    /// Response body/data as JSON string or raw content.
    /// </summary>
    public string? Body { get; init; }
    
    /// <summary>
    /// Parsed response data (for JSON responses).
    /// </summary>
    public object? Data { get; init; }
    
    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Response headers (for HTTP-based tasks).
    /// </summary>
    public Dictionary<string, string>? Headers { get; init; }
    
    /// <summary>
    /// Execution duration in milliseconds.
    /// </summary>
    public long ExecutionDurationMs { get; init; }
    
    /// <summary>
    /// Task type that was executed.
    /// </summary>
    public string? TaskType { get; init; }
    
    /// <summary>
    /// Additional metadata from execution.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
    
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="data">The response data object.</param>
    /// <param name="body">The raw response body as string.</param>
    /// <param name="statusCode">HTTP status code (default: 200).</param>
    /// <param name="executionDurationMs">Execution duration in milliseconds.</param>
    /// <param name="taskType">The task type identifier.</param>
    /// <param name="headers">Response headers.</param>
    /// <param name="metadata">Additional execution metadata.</param>
    public static TaskInvocationResult Success(
        object? data = null,
        string? body = null,
        int statusCode = 200,
        long executionDurationMs = 0,
        string? taskType = null,
        Dictionary<string, string>? headers = null,
        Dictionary<string, object>? metadata = null) => new()
    {
        IsSuccess = true,
        StatusCode = statusCode,
        Data = data,
        Body = body,
        ExecutionDurationMs = executionDurationMs,
        TaskType = taskType,
        Headers = headers,
        Metadata = metadata
    };
    
    /// <summary>
    /// Creates a failure result for error scenarios.
    /// Note: Infrastructure errors are captured here but should NOT fail the workflow.
    /// The error details are passed to output mapping so developers can handle them.
    /// </summary>
    /// <param name="error">The error message.</param>
    /// <param name="statusCode">HTTP status code if applicable.</param>
    /// <param name="body">Raw error response body.</param>
    /// <param name="executionDurationMs">Execution duration in milliseconds.</param>
    /// <param name="taskType">The task type identifier.</param>
    /// <param name="headers">Response headers (for HTTP error responses).</param>
    /// <param name="data">Parsed response data (for JSON error responses).</param>
    /// <param name="metadata">Additional error metadata (exception type, stack trace, etc.).</param>
    public static TaskInvocationResult Failure(
        string error,
        int? statusCode = null,
        string? body = null,
        long executionDurationMs = 0,
        string? taskType = null,
        Dictionary<string, string>? headers = null,
        object? data = null,
        Dictionary<string, object>? metadata = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = error,
        StatusCode = statusCode,
        Body = body,
        Data = data,
        ExecutionDurationMs = executionDurationMs,
        TaskType = taskType,
        Headers = headers,
        Metadata = metadata
    };
}

/// <summary>
/// Response wrapper for the invocation endpoint.
/// </summary>
public sealed class TaskInvokeResponse
{
    /// <summary>
    /// Indicates whether the task execution was successful.
    /// </summary>
    public bool Success { get; init; }
    
    /// <summary>
    /// Error message if execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// The result from task execution.
    /// </summary>
    public TaskInvocationResult? Result { get; init; }
    
    /// <summary>
    /// Total execution duration in milliseconds.
    /// </summary>
    public long ExecutionDurationMs { get; init; }
}

