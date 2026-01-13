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
        // Ensure consistent Code format for Task errors: Task:{Type}:Exception
        var taskNormalizedError = new NormalizedError
        {
            Code = $"Task:{taskType}:Exception",
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

        // Wrap with Task context
        var taskNormalizedError = new NormalizedError
        {
            Code = $"Task:{taskType}:{taskKey}",
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
