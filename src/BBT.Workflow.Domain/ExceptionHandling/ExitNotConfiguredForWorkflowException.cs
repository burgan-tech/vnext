using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when an exit transition is requested but the workflow does not have an exit configuration.
/// </summary>
public class ExitNotConfiguredForWorkflowException(string workflowKey) : UserFriendlyException(
    code: WorkflowErrorCodes.ExitNotConfiguredForWorkflow,
    message: $"This workflow \"{workflowKey}\" does not define an exit configuration");
