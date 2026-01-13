using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when an updateData transition is requested but the workflow does not have an updateData configuration.
/// </summary>
public class UpdateDataNotConfiguredForWorkflowException(string workflowKey) : UserFriendlyException(
    code: WorkflowErrorCodes.UpdateDataNotConfiguredForWorkflow,
    message: $"This workflow \"{workflowKey}\" does not define an updateData configuration");


