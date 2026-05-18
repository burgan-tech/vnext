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
}
