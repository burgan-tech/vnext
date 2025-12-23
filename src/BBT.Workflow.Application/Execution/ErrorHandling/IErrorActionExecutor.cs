using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Executes error actions based on resolved policies.
/// </summary>
public interface IErrorActionExecutor
{
    /// <summary>
    /// Applies the resolved error policy action.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="errorContext">The normalized error context.</param>
    /// <param name="resolvedPolicy">The resolved error policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of applying the action.</returns>
    Task<ErrorActionResult> ExecuteAsync(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        ResolvedPolicy resolvedPolicy,
        CancellationToken cancellationToken = default);
}

