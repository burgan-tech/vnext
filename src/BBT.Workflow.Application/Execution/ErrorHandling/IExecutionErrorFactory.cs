using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Factory for creating ExecutionError instances with consistent normalization.
/// </summary>
public interface IExecutionErrorFactory
{
    /// <summary>
    /// Creates an ExecutionError from an exception using IErrorNormalizer.
    /// </summary>
    ExecutionError CreateFromException(Exception exception, string taskKey, string taskType, long executionDurationMs);
    
    /// <summary>
    /// Creates an ExecutionError from an Error using IErrorNormalizer.
    /// </summary>
    ExecutionError CreateFromError(Error error, string taskKey, string taskType, long executionDurationMs);
    
    /// <summary>
    /// Creates an ExecutionError from a standard task response using IErrorNormalizer.
    /// </summary>
    ExecutionError CreateFromResponse(StandardTaskResponse response, string taskKey, string taskType, long executionDurationMs);
}
