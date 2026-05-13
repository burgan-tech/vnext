using BBT.Aether.Results;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Execution;

/// <summary>
/// Factory for creating TransitionExecutionContext instances.
/// Handles rehydration of workflow definitions, instances, and context setup.
/// </summary>
public interface ITransitionContextFactory
{
    /// <summary>
    /// Creates a new TransitionExecutionContext by rehydrating all necessary data from storage.
    /// </summary>
    /// <param name="context">The workflow execution context containing the request details.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A fully populated TransitionExecutionContext ready for execution.</returns>
    Task<Result<TransitionExecutionContext>> CreateAsync(WorkflowExecutionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a TransitionExecutionContext from pre-loaded instance and workflow definition
    /// without any database calls. Use when the caller already holds the required entities.
    /// </summary>
    /// <param name="input">The workflow execution context containing the request details.</param>
    /// <param name="workflow">Pre-loaded workflow definition.</param>
    /// <param name="instance">Pre-loaded instance entity.</param>
    /// <returns>A fully populated TransitionExecutionContext ready for validation.</returns>
    Result<TransitionExecutionContext> CreateFromPreloaded(
        WorkflowExecutionContext input,
        Definitions.Workflow workflow,
        Instance instance);
}
