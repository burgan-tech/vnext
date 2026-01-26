using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Represents a fatal domain registration error that occurs when the domain
/// cannot be registered with the service discovery registry during application startup.
/// This exception indicates a critical failure that prevents the domain from being
/// discoverable by other services in the distributed system.
/// </summary>
public sealed class DomainRegistrationFailedException(string domainName, string registryUrl, string reason)
    : UserFriendlyException(
        code: WorkflowErrorCodes.ValidationErrors,
        message: $"Failed to register domain '{domainName}' with service discovery registry at '{registryUrl}'. Reason: {reason}");
