using BBT.Workflow.Execution;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Resolves the <see cref="PipelineExecutionProfile"/> for a workflow transition based on trigger type and error-boundary semantics.
/// </summary>
public interface IPipelineProfileResolver
{
    /// <summary>
    /// Determines which pipeline execution profile applies to the given workflow execution context.
    /// </summary>
    /// <param name="context">The inbound workflow execution context (trigger type, error boundary flags, etc.).</param>
    /// <returns>The profile defining which lifecycle steps may be skipped.</returns>
    PipelineExecutionProfile Resolve(WorkflowExecutionContext context);

    /// <summary>
    /// Determines the pipeline execution profile once the transition context has been built.
    /// Resolves the same base profile as <see cref="Resolve(WorkflowExecutionContext)"/> and then
    /// composes the self-target variant when the transition's target resolves to the state the
    /// instance is already in — a transition that changes no state must not run the state's
    /// lifecycle steps.
    /// </summary>
    /// <param name="context">The inbound workflow execution context, which owns the base trigger semantics.</param>
    /// <param name="transitionContext">The built transition context, which knows the resolved transition and current state.</param>
    /// <returns>The profile defining which lifecycle steps may be skipped.</returns>
    PipelineExecutionProfile Resolve(WorkflowExecutionContext context, TransitionExecutionContext transitionContext);
}
