using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Defines behavior when a resource lock cannot be acquired.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceLockConflictPolicy
{
    /// <summary>
    /// Abort the transition immediately when the lock cannot be acquired.
    /// </summary>
    Abort = 0
}
