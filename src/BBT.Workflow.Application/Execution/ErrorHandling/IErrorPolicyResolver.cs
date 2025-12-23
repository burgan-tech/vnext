using BBT.Workflow.Definitions;

namespace BBT.Workflow.Execution.ErrorHandling;

/// <summary>
/// Resolves error handling policies from the Task → State → Global hierarchy.
/// </summary>
public interface IErrorPolicyResolver
{
    /// <summary>
    /// Resolves the appropriate error policy for the given context and error.
    /// Searches through Task → State → Global boundaries in order.
    /// </summary>
    /// <param name="context">The transition execution context.</param>
    /// <param name="errorContext">The normalized error context.</param>
    /// <param name="onExecuteTask">The task execution configuration that caused the error (null for non-task errors).</param>
    /// <returns>The resolved policy, or null if no matching policy found (unhandled error).</returns>
    ResolvedPolicy? Resolve(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        OnExecuteTask? onExecuteTask = null);

    /// <summary>
    /// Resolves error policy for SubFlow propagation.
    /// Uses the SubFlow state's error boundary for parent-side handling.
    /// </summary>
    /// <param name="context">The parent's transition execution context.</param>
    /// <param name="errorContext">The error context from child workflow.</param>
    /// <param name="subFlowState">The state containing the SubFlow configuration.</param>
    /// <returns>The resolved policy, or null if no matching policy found.</returns>
    ResolvedPolicy? ResolveSubFlow(
        TransitionExecutionContext context,
        ErrorContext errorContext,
        State subFlowState);
}

