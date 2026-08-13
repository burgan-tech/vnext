using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Default implementation of IExecutionErrorFactory.
/// Uses IErrorNormalizer to ensure consistent error logic.
/// </summary>
public sealed class ExecutionErrorFactory : IExecutionErrorFactory
{
    private readonly IErrorNormalizer _errorNormalizer;

    public ExecutionErrorFactory(IErrorNormalizer errorNormalizer)
    {
        _errorNormalizer = errorNormalizer;
    }

    /// <inheritdoc />
    public ExecutionError CreateFromException(Exception exception, string taskKey, string taskType, long executionDurationMs)
    {
        var normalized = _errorNormalizer.Normalize(exception);
        
        // Wrap with Task context
        // Ensure consistent Code format for Task errors: Task:{Type}:Exception[:{Status}]
        var taskNormalizedError = new NormalizedError
        {
            Code = normalized.StatusCode is { } exceptionStatus
                ? $"Task:{taskType}:Exception:{exceptionStatus}"
                : $"Task:{taskType}:Exception",
            Layer = ErrorLayer.Task,
            ExceptionType = normalized.ExceptionType,
            StatusCode = normalized.StatusCode,
            Message = normalized.Message,
            Source = normalized.Source,
            IsTransient = normalized.IsTransient,
            OriginalCode = normalized.OriginalCode
        };

        return new ExecutionError
        {
            TaskKey = taskKey,
            TaskType = taskType,
            StatusCode = normalized.StatusCode,
            ErrorMessage = exception.Message,
            StackTrace = exception.StackTrace,
            NormalizedError = taskNormalizedError,
            ExecutionDurationMs = executionDurationMs,
            Metadata = new Dictionary<string, object>
            {
                ["ExceptionType"] = normalized.ExceptionType ?? exception.GetType().Name,
                ["StackTrace"] = exception.StackTrace ?? string.Empty
            }
        };
    }

    /// <inheritdoc />
    public ExecutionError CreateFromError(Error error, string taskKey, string taskType, long executionDurationMs)
    {
        var normalized = _errorNormalizer.Normalize(error);

        // Wrap with Task context — unless the error already carries it. An engine-level failure
        // arrives here a second time (engine → ExecutionError.ToError() → Result.Fail → the
        // coordinator re-wraps it), and the outer call site does not know the task type: naively
        // rebuilding would flatten a precise `Task:DirectTrigger:trigger-transition:409` into
        // `Task:Unknown:trigger-transition`, losing exactly the status an error boundary needs to
        // separate a retryable conflict from a terminal one.
        var taskNormalizedError = new NormalizedError
        {
            Code = BuildTaskCode(error.Code, taskType, taskKey, normalized.StatusCode),
            Layer = ErrorLayer.Task,
            ExceptionType = normalized.ExceptionType,
            StatusCode = normalized.StatusCode,
            Message = normalized.Message,
            Source = normalized.Source,
            IsTransient = normalized.IsTransient,
            OriginalCode = normalized.OriginalCode
        };

        return new ExecutionError
        {
            TaskKey = taskKey,
            TaskType = taskType,
            StatusCode = normalized.StatusCode,
            ErrorMessage = error.Message,
            NormalizedError = taskNormalizedError,
            ExecutionDurationMs = executionDurationMs
        };
    }

    /// <summary>
    /// Builds the task-scoped error code: keeps an already task-scoped code verbatim, otherwise
    /// composes <c>Task:{type}:{key}</c> and appends the resolved status when one is known, so the
    /// shape matches what <see cref="CreateFromResponse"/> and <c>ExecutionError.ToError</c> emit.
    /// </summary>
    private static string BuildTaskCode(string? originalCode, string taskType, string taskKey, int? statusCode)
    {
        if (originalCode is { Length: > 0 } code
            && code.StartsWith(ErrorNormalizer.TaskErrorCodePrefix, StringComparison.Ordinal))
        {
            return code;
        }

        var built = $"Task:{taskType}:{taskKey}";
        return statusCode.HasValue ? $"{built}:{statusCode.Value}" : built;
    }

    /// <inheritdoc />
    public ExecutionError CreateFromResponse(StandardTaskResponse response, string taskKey, string taskType, long executionDurationMs)
    {
        var normalizedError = _errorNormalizer.NormalizeTaskResponse(response, taskKey, taskType);
         
        return new ExecutionError
        {
            TaskKey = taskKey,
            TaskType = taskType,
            StatusCode = response.StatusCode,
            ErrorMessage = response.ErrorMessage,
            NormalizedError = normalizedError,
            ExecutionDurationMs = executionDurationMs,
            Metadata = response.Metadata
        };
    }
}
