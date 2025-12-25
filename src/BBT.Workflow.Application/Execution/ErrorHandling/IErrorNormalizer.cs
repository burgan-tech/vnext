using BBT.Aether.Results;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Service for normalizing errors from different sources into a unified format.
/// Enables consistent error boundary matching across Result, TaskInvocationResult, and StandardTaskResponse.
/// </summary>
public interface IErrorNormalizer
{
    /// <summary>
    /// Normalizes error from a Result failure.
    /// </summary>
    /// <param name="error">The Result error.</param>
    /// <returns>Normalized error representation.</returns>
    NormalizedError FromResult(Error error);
    
    /// <summary>
    /// Normalizes error from a TaskInvocationResult failure.
    /// </summary>
    /// <param name="result">The failed TaskInvocationResult.</param>
    /// <returns>Normalized error representation.</returns>
    NormalizedError FromTaskInvocationResult(TaskInvocationResult result);
    
    /// <summary>
    /// Normalizes error from a StandardTaskResponse failure.
    /// </summary>
    /// <param name="response">The failed StandardTaskResponse.</param>
    /// <returns>Normalized error representation.</returns>
    NormalizedError FromStandardResponse(StandardTaskResponse response);
    
    /// <summary>
    /// Normalizes error from an exception.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <param name="layer">The layer where exception occurred.</param>
    /// <returns>Normalized error representation.</returns>
    NormalizedError FromException(Exception exception, ErrorLayer layer = ErrorLayer.Pipeline);
    
    /// <summary>
    /// Extracts all errors from a task execution result.
    /// Returns empty collection if no errors are present.
    /// </summary>
    /// <param name="result">The Result containing StandardTaskResponse.</param>
    /// <returns>Collection of normalized errors, ordered by severity.</returns>
    IReadOnlyList<NormalizedError> ExtractErrors(Result<StandardTaskResponse> result);
    
    /// <summary>
    /// Gets the primary error from a task execution result.
    /// Returns null if execution was successful.
    /// </summary>
    /// <param name="result">The Result containing StandardTaskResponse.</param>
    /// <returns>The primary normalized error, or null if successful.</returns>
    NormalizedError? GetPrimaryError(Result<StandardTaskResponse> result);
}

