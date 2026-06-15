using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Specifies the lock operation to perform during a transition.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceLockAction
{
    /// <summary>
    /// Acquires a distributed lock on the resource.
    /// Fails the transition if the resource is already locked by another owner.
    /// </summary>
    Acquire = 0,

    /// <summary>
    /// Releases a previously acquired distributed lock on the resource.
    /// </summary>
    Release = 1,

    /// <summary>
    /// Extends the TTL of an existing lock held by the same owner.
    /// </summary>
    Extend = 2
}
