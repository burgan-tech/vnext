using BBT.Aether.Results;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Represents an error from task execution with full context for Error Boundary resolution.
/// Carries the normalized error information along with task-specific details
/// for policy resolution and error handling.
/// </summary>
public sealed record ExecutionError
{
    /// <summary>
    /// Gets the key of the task that failed.
    /// </summary>
    public required string TaskKey { get; init; }

    /// <summary>
    /// Gets the type of task that failed (e.g., "Http", "Dapr", "Script").
    /// </summary>
    public required string TaskType { get; init; }

    /// <summary>
    /// Gets the HTTP status code if applicable.
    /// </summary>
    public int? StatusCode { get; init; }

    /// <summary>
    /// Gets the error message from task execution.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the normalized error for Error Boundary policy matching.
    /// </summary>
    public required NormalizedError NormalizedError { get; init; }

    /// <summary>
    /// Gets the execution duration in milliseconds.
    /// </summary>
    public long ExecutionDurationMs { get; init; }

    /// <summary>
    /// Gets additional metadata from the task execution.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Converts this ExecutionError to an Error for Result pattern propagation.
    /// The error code format preserves both StatusCode and ExceptionType for Error Boundary matching:
    /// - Task:Type:StatusCode (when only status code is available)
    /// - Task:Type:ExceptionType (when only exception type is available)
    /// - Task:Type:StatusCode:ExceptionType (when both are available)
    /// </summary>
    /// <returns>An Error representing this task execution failure.</returns>
    public Error ToError()
    {
        var code = $"Task:{TaskType}:{TaskKey}";

        if (StatusCode.HasValue)
        {
            code = $"{code}:{StatusCode.Value}";
        }

        // Append ExceptionType if available (from NormalizedError or Metadata)
        var exceptionType = NormalizedError.ExceptionType
            ?? (Metadata?.TryGetValue("ExceptionType", out var et) == true ? et.ToString() : null);

        if (!string.IsNullOrEmpty(exceptionType))
        {
            code = $"{code}:{exceptionType}";
        }

        return Error.Failure(code, ErrorMessage ?? "Task execution failed");
    }


}

