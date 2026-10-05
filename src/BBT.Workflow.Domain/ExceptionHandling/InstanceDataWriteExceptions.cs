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

/// <summary>
/// An <c>x-encryption.type: "encrypt"</c> value of the instance cannot be decrypted with this host's
/// keyring. The engine refuses to act on the token string; restoring the key recovers. Maps to HTTP 503.
/// </summary>
public class EncryptionKeyUnavailableException(Guid instanceId, string path) : UserFriendlyException(
    code: WorkflowErrorCodes.EncryptionKeyUnavailable,
    message: $"An encrypted field of instance \"{instanceId}\" cannot be decrypted (path \"{path}\"); its key is not available on this host")
{
    /// <summary>The first undecryptable path.</summary>
    public string Path { get; } = path;
}

/// <summary>
/// A request introduced a string carrying the reserved <c>ENCRYPTED:AES256:</c> prefix at a path where it is not
/// the token already stored. The value itself is never echoed. Maps to HTTP 400.
/// </summary>
public class EncryptedValueReservedException(string path) : UserFriendlyException(
    code: WorkflowErrorCodes.EncryptedValueReserved,
    message: $"The value at \"{path}\" carries a reserved x-encryption prefix (\"ENCRYPTED:AES256:\" or \"HASHED:\"); only the value already stored at that path may be sent back")
{
    /// <summary>The offending path.</summary>
    public string Path { get; } = path;
}

/// <summary>
/// The master schema could not be resolved while field encryption is configured; the write is refused so that
/// nothing is stored in plaintext by accident. Transient. Maps to HTTP 503.
/// </summary>
public class EncryptionSchemaUnavailableException(string schemaKey) : UserFriendlyException(
    code: WorkflowErrorCodes.EncryptionSchemaUnavailable,
    message: $"Master schema \"{schemaKey}\" could not be resolved; the instance data write is refused because field encryption is configured")
{
}
