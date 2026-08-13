using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.DefinitionContext;

/// <summary>
/// Scoped implementation of <see cref="IWorkflowContext"/> that holds the current workflow
/// definition for the duration of a single DI scope (an HTTP request, a background job, or a
/// migration run).
/// </summary>
/// <remarks>
/// In the API hosts the workflow is set by the WorkflowValidationFilter early in the request
/// pipeline and remains available throughout the request. In non-HTTP hosts (workers, the
/// DbMigrator) nothing sets it, and consumers such as the InstanceData write service treat the
/// empty context as "no workflow — skip workflow-bound behavior" (e.g. master-schema validation).
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
