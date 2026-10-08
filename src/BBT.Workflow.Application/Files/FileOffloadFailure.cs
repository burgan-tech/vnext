using BBT.Aether.Results;
using BBT.Workflow.ExceptionHandling;

namespace BBT.Workflow.Files;

/// <summary>
/// Turns a failed <see cref="IFileOffloadService.OffloadAsync"/> result into the exception a pipeline write throws
/// (spec §3: inside a pipeline — mapping step, data write funnel — the failure follows the normal error path).
/// </summary>
public static class FileOffloadFailure
{
    /// <summary>
    /// <see cref="WorkflowErrorCodes.FileStoreUnavailable"/> ⇒ <see cref="FileStoreUnavailableException"/> (503,
    /// carrying the binding component); anything else ⇒ <see cref="FileReferenceInvalidException"/> (400, carrying
    /// the offending path).
    /// </summary>
    public static Exception ToException(Error error)
    {
        var target = error.Target ?? string.Empty;
        if (error.Code == WorkflowErrorCodes.FileStoreUnavailable)
            return new FileStoreUnavailableException(target);

        // WorkflowErrors.FileReferenceInvalid already prefixes the message with the path; keep only the reason so the
        // exception does not repeat it.
        var prefix = $"The file at \"{target}\" is invalid: ";
        var message = error.Message ?? string.Empty;
        var reason = message.StartsWith(prefix, StringComparison.Ordinal) ? message[prefix.Length..] : message;
        return new FileReferenceInvalidException(target, reason);
    }
}
