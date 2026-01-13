namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Normalized error information for consistent error boundary matching.
/// Unifies Result, TaskInvocationResult and StandardTaskResponse error representations.
/// </summary>
/// <remarks>
/// Error sources have different representations:
/// - Result&lt;T&gt;.IsSuccess → Pipeline/transport/exception level
/// - TaskInvocationResult.IsSuccess → Task execution result
/// - StandardTaskResponse.IsSuccess + StatusCode + Error → Response level
/// 
/// This model normalizes all sources into a consistent format for boundary matching.
/// </remarks>
public sealed class NormalizedError
{
    /// <summary>
    /// Normalized error code in format: {Layer}:{Category}:{Detail}
    /// Examples: "Task:Http:503", "Transport:Dapr:Timeout", "Pipeline:Mapping:Input"
    /// </summary>
    public required string Code { get; init; }
    
    /// <summary>
    /// Error layer for category-based matching.
    /// </summary>
    public ErrorLayer Layer { get; init; }
    
    /// <summary>
    /// Exception type name for type-based matching.
    /// Examples: "TimeoutException", "HttpRequestException"
    /// </summary>
    public string? ExceptionType { get; init; }
    
    /// <summary>
    /// HTTP status code if applicable.
    /// Used for StatusCode-based boundary matching.
    /// </summary>
    public int? StatusCode { get; init; }
    
    /// <summary>
    /// Human-readable error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;
    
    /// <summary>
    /// Original source of the error.
    /// </summary>
    public ErrorSource Source { get; init; }
    
    /// <summary>
    /// Indicates if this error is typically transient and retryable.
    /// </summary>
    public bool IsTransient { get; init; }
    
    /// <summary>
    /// Original error code before normalization (if different from Code).
    /// </summary>
    public string? OriginalCode { get; init; }
    
    /// <summary>
    /// Gets the status code as string for code matching.
    /// </summary>
    public string? StatusCodeString => StatusCode?.ToString();
    
    /// <summary>
    /// Gets all matchable codes (normalized code, status code string, original code).
    /// </summary>
    public IEnumerable<string> GetMatchableCodes()
    {
        yield return Code;
        
        if (StatusCode.HasValue)
            yield return StatusCode.Value.ToString();
        
        if (!string.IsNullOrEmpty(OriginalCode) && OriginalCode != Code)
            yield return OriginalCode;
    }
}

/// <summary>
/// Error layer indicating where the error originated.
/// </summary>
public enum ErrorLayer
{
    /// <summary>
    /// Transport layer errors: Dapr communication, HTTP client, network issues.
    /// These are infrastructure-level errors before task execution.
    /// </summary>
    Transport,
    
    /// <summary>
    /// Task layer errors: Task execution results from external services.
    /// These are errors returned by the actual task (HTTP 4xx/5xx, etc.).
    /// </summary>
    Task,
    
    /// <summary>
    /// Pipeline layer errors: Mapping, validation, coordination errors.
    /// These are orchestration-level errors during pipeline execution.
    /// </summary>
    Pipeline
}

/// <summary>
/// Source of the error indicating which level reported the failure.
/// </summary>
public enum ErrorSource
{
    /// <summary>
    /// Error from Result&lt;T&gt;.IsSuccess = false.
    /// Typically transport/pipeline exceptions or explicit failures.
    /// </summary>
    ResultFailure,
    
    /// <summary>
    /// Error from TaskInvocationResult.IsSuccess = false.
    /// Task execution returned a failure status.
    /// </summary>
    TaskInvocationFailure,
    
    /// <summary>
    /// Error inferred from HTTP StatusCode >= 400.
    /// Task succeeded but returned error status code.
    /// </summary>
    ResponseStatusCode,
    
    /// <summary>
    /// Error from StandardTaskResponse.Error property.
    /// Explicit error in the response model.
    /// </summary>
    ResponseError,
    
    /// <summary>
    /// Error from exception during execution.
    /// </summary>
    Exception
}

