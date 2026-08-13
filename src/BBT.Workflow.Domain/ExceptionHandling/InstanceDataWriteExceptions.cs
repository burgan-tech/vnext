using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// The per-instance <c>FOR UPDATE</c> row lock guarding an InstanceData write could not be
/// acquired within <c>lock_timeout</c> — a concurrent writer (typically an updateData request
/// racing a running pipeline) held it for the whole wait budget. Transient: the caller can
/// retry. Maps to HTTP 409.
/// </summary>
public class InstanceDataLockTimeoutException(Guid instanceId) : UserFriendlyException(
    code: WorkflowErrorCodes.InstanceDataLockTimeout,
    message: $"Instance data write lock could not be acquired for instance \"{instanceId}\"; a concurrent write is in progress");

/// <summary>
/// An InstanceData write statement exceeded <c>statement_timeout</c> and was cancelled by
/// PostgreSQL. Transient server-side pressure: the transaction was rolled back and the caller
/// can retry. Maps to HTTP 503 so retrying relays treat it as transient.
/// </summary>
public class InstanceDataWriteTimeoutException(Guid instanceId) : UserFriendlyException(
    code: WorkflowErrorCodes.InstanceDataWriteTimeout,
    message: $"Instance data write timed out for instance \"{instanceId}\"");
