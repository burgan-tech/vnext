namespace BBT.Workflow.Tasks;

/// <summary>
/// Unified result from task invocation.
/// Works for both local and remote execution modes.
/// Independent of Domain types for serialization across service boundaries.
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
    /// Domain error code for error boundary matching (e.g., "Task:400007", "Dapr.Invocation").
    /// </summary>
    public string? ErrorCode { get; init; }
    
    /// <summary>
    /// Exception type name for error boundary matching (e.g., "HttpRequestException", "TimeoutException").
    /// </summary>
    public string? ExceptionTypeName { get; init; }
    
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
    /// Creates a failure result.
    /// </summary>
    public static TaskInvocationResult Failure(
        string error,
        int? statusCode = null,
        string? body = null,
        long executionDurationMs = 0,
        string? taskType = null,
        Dictionary<string, string>? headers = null,
        object? data = null,
        Dictionary<string, object>? metadata = null,
        string? errorCode = null,
        string? exceptionTypeName = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = error,
        ErrorCode = errorCode,
        ExceptionTypeName = exceptionTypeName,
        StatusCode = statusCode,
        Body = body,
        Data = data,
        Headers = headers,
        ExecutionDurationMs = executionDurationMs,
        TaskType = taskType,
        Metadata = metadata
    };
}

/// <summary>
/// Response wrapper for remote task invocation endpoint.
/// Used for serialization across service boundaries.
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

/// <summary>
/// Request wrapper for remote task invocation endpoint.
/// </summary>
public sealed class TaskInvokeRequest
{
    /// <summary>
    /// The task envelope containing type and binding.
    /// </summary>
    public required TaskEnvelope Envelope { get; init; }
    
    /// <summary>
    /// Trace context for distributed tracing.
    /// </summary>
    public TaskTraceContext? TraceContext { get; init; }
}

