using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.DefinitionContext;

/// <summary>
/// Scoped implementation of <see cref="IWorkflowContext"/> that holds the current workflow
/// definition for the duration of a single HTTP request.
/// </summary>
/// <remarks>
/// This class is registered as Scoped in DI, ensuring each request gets its own instance.
/// The workflow is set by <see cref="Microsoft.AspNetCore.Mvc.Filters.WorkflowValidationFilter"/>
/// early in the request pipeline and remains available throughout the request lifecycle.
/// </remarks>
public sealed class WorkflowContext : IWorkflowContext
{
    private WorkflowDefinition? _workflow;

    /// <inheritdoc />
    public WorkflowDefinition? Workflow => _workflow;

    /// <inheritdoc />
    public bool HasWorkflow => _workflow is not null;

    /// <inheritdoc />
    public void SetWorkflow(WorkflowDefinition workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
    }
}
