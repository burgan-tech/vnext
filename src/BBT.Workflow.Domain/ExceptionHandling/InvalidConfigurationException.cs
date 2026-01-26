using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Represents a fatal configuration error that should stop the application startup.
/// </summary>
public sealed class InvalidConfigurationException(string message)
    : UserFriendlyException(code: WorkflowErrorCodes.ValidationErrors, message: message);
