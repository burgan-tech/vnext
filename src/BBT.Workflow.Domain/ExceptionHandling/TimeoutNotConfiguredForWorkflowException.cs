using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when a timeout transition is requested but the workflow does not have a timeout configuration.
/// </summary>
public class TimeoutNotConfiguredForWorkflowException(string workflowKey) : UserFriendlyException(
    code: WorkflowErrorCodes.TimeoutConfigMissing,
    message: $"This workflow \"{workflowKey}\" does not define a timeout configuration");
