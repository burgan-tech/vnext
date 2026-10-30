using System.Text.Json.Serialization;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Defines behavior when a resource lock cannot be acquired.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResourceLockConflictPolicy
{
    /// <summary>
    /// Abort the transition immediately when the lock cannot be acquired: the instance is FAULTED and
    /// the caller gets 409. The contended work is lost — a retry means a new instance.
    /// </summary>
    Abort = 0,

    /// <summary>
    /// Reject the attempt without touching the instance: no fault, no state change, the Busy flip is
    /// compensated and the caller gets 409 with a distinct, retryable error code. The instance stays
    /// exactly where it was, so the SAME call can simply be retried once the lock frees.
    /// Use when losing a race is a normal, repeatable condition rather than a failure.
    /// </summary>
    Reject = 1
}
