using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.DefinitionContext;

/// <summary>
/// Provides access to the current workflow context within a request scope.
/// This context is populated early in the request pipeline and can be
/// injected into any service that needs access to the current workflow.
/// </summary>
public interface IWorkflowContext
{
    /// <summary>
    /// Gets the current workflow definition.
    /// Returns null if no workflow has been loaded for this request.
    /// </summary>
    WorkflowDefinition? Workflow { get; }

    /// <summary>
    /// Gets whether a workflow has been loaded for this request.
    /// </summary>
    bool HasWorkflow { get; }

    /// <summary>
    /// Sets the workflow for the current request scope.
    /// </summary>
    /// <param name="workflow">The workflow definition to set</param>
    void SetWorkflow(WorkflowDefinition workflow);
}
