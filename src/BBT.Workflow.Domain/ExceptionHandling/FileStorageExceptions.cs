using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>Binding create/get failed inside a pipeline write. Transient. Maps to HTTP 503.</summary>
public class FileStoreUnavailableException(string component) : UserFriendlyException(
    code: WorkflowErrorCodes.FileStoreUnavailable,
    message: $"File store \"{component}\" is unavailable; the request was not applied and can be retried");

/// <summary>A malformed x-storage node reached a pipeline write. Maps to HTTP 400.</summary>
public class FileReferenceInvalidException(string path, string reason) : UserFriendlyException(
    code: WorkflowErrorCodes.FileReferenceInvalid,
    message: $"The file at \"{path}\" is invalid: {reason}");
