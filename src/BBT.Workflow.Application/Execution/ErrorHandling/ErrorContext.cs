using BBT.Aether.Results;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Normalized representation of an error for policy matching.
/// Provides a consistent view of errors regardless of source (exception, Result.Fail, timeout).
/// </summary>
public sealed record ErrorContext
{
    /// <summary>
    /// The original exception, if available.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// The Error from Result pattern, if available.
    /// </summary>
    public Error? Error { get; init; }

    /// <summary>
    /// The normalized exception type name for matching (e.g., "ValidationException").
    /// </summary>
    public string ExceptionTypeName { get; init; } = string.Empty;

    /// <summary>
    /// The domain error code for matching (e.g., "Task:400007", "Validation:100001").
    /// Null if not applicable.
    /// </summary>
    public string? ErrorCode { get; init; }

    /// <summary>
    /// The HTTP status code from the response, if applicable.
    /// Used for StatusCode-based error boundary matching (e.g., 503, 502, 429).
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Indicates whether this error is a timeout.
    /// </summary>
    public bool IsTimeout { get; init; }

    /// <summary>
    /// The scope where the error was caught.
    /// </summary>
    public ErrorBoundaryScope Scope { get; init; }

    /// <summary>
    /// The task key where the error occurred, if applicable.
    /// </summary>
    public string? TaskKey { get; init; }

    /// <summary>
    /// The state key where the error occurred.
    /// </summary>
    public string? StateKey { get; init; }

    /// <summary>
    /// The transition key being executed when the error occurred.
    /// </summary>
    public string? TransitionKey { get; init; }

    /// <summary>
    /// The workflow instance ID.
    /// </summary>
    public Guid InstanceId { get; init; }

    /// <summary>
    /// The domain of the workflow.
    /// </summary>
    public string Domain { get; init; } = string.Empty;

    /// <summary>
    /// The workflow key.
    /// </summary>
    public string Flow { get; init; } = string.Empty;

    /// <summary>
    /// The current retry attempt (1-based). 0 means first execution (not a retry).
    /// </summary>
    public int Attempt { get; init; }

    /// <summary>
    /// The error message for logging and display.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Additional error details (stack trace, inner exception, etc.).
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Timestamp when the error occurred.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Normalized error representation for consistent boundary matching.
    /// Unifies Result, TaskInvocationResult, and StandardTaskResponse errors.
    /// </summary>
    public NormalizedError? NormalizedError { get; init; }

    /// <summary>
    /// Gets a value indicating whether this context has exception information.
    /// </summary>
    public bool HasException => Exception != null;

    /// <summary>
    /// Gets a value indicating whether this context has Result error information.
    /// </summary>
    public bool HasResultError => Error != null;

    /// <summary>
    /// Creates an ErrorContext for an unhandled exception.
    /// </summary>
    public static ErrorContext FromException(Exception ex, ErrorBoundaryScope scope)
    {
        return new ErrorContext
        {
            Exception = ex,
            ExceptionTypeName = ex.GetType().Name,
            ErrorCode = ExtractErrorCode(ex),
            IsTimeout = IsTimeoutException(ex),
            Scope = scope,
            Message = ex.Message,
            Details = ex.StackTrace
        };
    }

    /// <summary>
    /// Creates an ErrorContext from a Result error.
    /// </summary>
    public static ErrorContext FromError(Error error, ErrorBoundaryScope scope)
    {
        var errorString = error.ToString();
        return new ErrorContext
        {
            Error = error,
            ExceptionTypeName = MapErrorStringToExceptionName(errorString),
            ErrorCode = error.Code,
            IsTimeout = errorString?.Contains("timeout", StringComparison.OrdinalIgnoreCase) ?? false,
            Scope = scope,
            Message = errorString ?? string.Empty,
            Details = null
        };
    }

    private static string? ExtractErrorCode(Exception ex)
    {
        return ex switch
        {
            IHasErrorCode hasCode => hasCode.ErrorCode.ToString(),
            HttpRequestException httpEx => httpEx.StatusCode?.ToString(),
            _ => null
        };
    }

    private static bool IsTimeoutException(Exception ex)
    {
        return ex is TimeoutException ||
               ex is TaskCanceledException tce && tce.CancellationToken.IsCancellationRequested == false ||
               ex is OperationCanceledException oce && oce.CancellationToken.IsCancellationRequested == false ||
               ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapErrorStringToExceptionName(string? errorString)
    {
        if (string.IsNullOrEmpty(errorString)) return "Exception";

        // Map common error patterns to exception names
        return errorString.ToLowerInvariant() switch
        {
            var c when c.Contains("validation") => "ValidationException",
            var c when c.Contains("notfound") || c.Contains("not_found") => "NotFoundException",
            var c when c.Contains("conflict") => "ConflictException",
            var c when c.Contains("forbidden") => "ForbiddenException",
            var c when c.Contains("unauthorized") => "UnauthorizedException",
            var c when c.Contains("timeout") => "TimeoutException",
            _ => "FailureException"
        };
    }
}

