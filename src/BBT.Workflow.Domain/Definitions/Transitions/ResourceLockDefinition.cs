using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Defines a resource lock operation to be executed during a transition.
/// The <see cref="KeyExpression"/> is compiled as <c>ITransitionMapping</c> and evaluated
/// at runtime to produce the distributed lock key.
/// </summary>
public sealed class ResourceLockDefinition
{
    [JsonConstructor]
    public ResourceLockDefinition(
        ScriptCode keyExpression,
        ResourceLockAction action,
        int ttlSeconds = 300,
        ResourceLockConflictPolicy onConflict = ResourceLockConflictPolicy.Abort)
    {
        KeyExpression = keyExpression ?? throw new ArgumentNullException(nameof(keyExpression));
        Action = action;
        TtlSeconds = ttlSeconds;
        OnConflict = onConflict;
    }

    /// <summary>
    /// Script compiled as <c>ITransitionMapping</c> whose <c>Handler</c> returns the lock key string.
    /// </summary>
    public ScriptCode KeyExpression { get; }

    /// <summary>
    /// The lock operation to perform: Acquire, Release, or Extend.
    /// </summary>
    public ResourceLockAction Action { get; }

    /// <summary>
    /// Time-to-live in seconds for Acquire and Extend operations.
    /// Acts as a safety net so abandoned locks expire automatically.
    /// </summary>
    public int TtlSeconds { get; }

    /// <summary>
    /// Policy applied when the lock cannot be acquired (e.g., resource already locked).
    /// </summary>
    public ResourceLockConflictPolicy OnConflict { get; }
}
