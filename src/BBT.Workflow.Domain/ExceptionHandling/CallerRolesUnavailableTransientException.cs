using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// A SubFlow output mapping read the caller's roles while the caller-role provider could not answer.
/// Transient (<c>Authorization:110006</c>, HTTP 503): thrown out of the completion so its unit of work rolls
/// back and the terminal event is redelivered (post-commit relay retry or the Inbox backup) — a role-provider
/// blip must never fault the parent permanently, which is what a failed mapping result would do.
/// </summary>
public sealed class CallerRolesUnavailableTransientException(Guid parentInstanceId)
    : UserFriendlyException(
        code: WorkflowErrorCodes.CallerRolesUnavailable,
        message: $"The caller's roles could not be resolved for the SubFlow output mapping of parent instance \"{parentInstanceId}\"; the completion will be redelivered.");
