using BBT.Aether.Results;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Normalizes errors from various sources into a consistent format for boundary matching.
/// </summary>
public interface IErrorNormalizer
{
    /// <summary>
    /// Normalizes an exception into a standard error format.
    /// </summary>
    /// <param name="exception">The exception to normalize.</param>
    /// <returns>A normalized error representation.</returns>
    NormalizedError Normalize(Exception exception);

    /// <summary>
    /// Normalizes a Result Error into a standard error format.
    /// </summary>
    /// <param name="error">The error to normalize.</param>
    /// <returns>A normalized error representation.</returns>
    NormalizedError Normalize(Error error);

    /// <summary>
    /// Normalizes an HTTP response error.
    /// </summary>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="errorCode">The error code from the response.</param>
    /// <param name="message">The error message.</param>
    /// <returns>A normalized error representation.</returns>
    NormalizedError NormalizeHttpError(int statusCode, string? errorCode, string? message);

    /// <summary>
    /// Normalizes a TaskInvocationResult into a standard error format.
    /// Extracts StatusCode, ErrorMessage, and Metadata for policy resolution.
    /// </summary>
    /// <param name="result">The task invocation result.</param>
    /// <param name="taskType">The type of task that was executed.</param>
    /// <returns>A normalized error representation.</returns>
    NormalizedError NormalizeTaskResult(TaskInvocationResult result, string taskType);

    /// <summary>
    /// Normalizes a StandardTaskResponse into a standard error format.
    /// Extracts StatusCode, ErrorMessage, and Metadata (including ExceptionType) for Error Boundary policy resolution.
    /// </summary>
    /// <param name="response">The task response containing error details.</param>
    /// <param name="taskKey">The key of the task that produced the response.</param>
    /// <param name="taskType">The type of task that was executed.</param>
    /// <returns>A normalized error representation.</returns>
    NormalizedError NormalizeTaskResponse(StandardTaskResponse response, string taskKey, string taskType);
}

