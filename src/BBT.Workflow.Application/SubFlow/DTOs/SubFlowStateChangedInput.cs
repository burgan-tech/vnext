using BBT.Workflow.Definitions;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Data payload for SubFlow state change event.
/// Contains all necessary information to update the parent instance's correlation and EffectiveState.
/// </summary>
public record SubFlowStateChangedInput
{
    /// <summary>
    /// The ID of the Parent instance
    /// </summary>
    public required Guid ParentInstanceId { get; init; }

    /// <summary>
    /// The ID of the SubFlow instance that changed state
    /// </summary>
    public required Guid SubInstanceId { get; init; }

    /// <summary>
    /// The domain of the parent workflow
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the parent
    /// </summary>
    public required string Flow { get; init; }

    /// <summary>
    /// The version of the parent workflow
    /// </summary>
    public required string? Version { get; init; }

    /// <summary>
    /// The new state after the change
    /// </summary>
    public required string NewState { get; init; }

    /// <summary>
    /// The previous state before the change
    /// </summary>
    public required string? PreviousState { get; init; }

    /// <summary>
    /// Type of the new state
    /// Used for upward propagation to parent instance
    /// </summary>
    public required StateType NewStateType { get; init; }

    /// <summary>
    /// Subtype of the new state
    /// Used for upward propagation to parent instance and automated status handling
    /// </summary>
    public required StateSubType NewStateSubType { get; init; }

    /// <summary>
    /// When the state change occurred
    /// </summary>
    public required DateTime ChangedAt { get; init; }

    /// <summary>
    /// The sub-item's effective status at rest. Null when the publisher reported none, which leaves
    /// the parent's <c>EffectiveStatus</c> untouched rather than guessing one.
    /// </summary>
    public string? NewStatus { get; init; }

    /// <summary>
    /// Per-instance strictly increasing notification number from the sub-item; the ordering
    /// authority. Zero means "not reported" and the receiver falls back to <see cref="ChangedAt"/>.
    /// </summary>
    public long NotificationSeq { get; init; }

    /// <summary>
    /// Lane anchor of the child that published the change — see
    /// <c>ILaneAwareDistributedEvent.TraceRoot</c>. Internal-only, never read from a request header:
    /// a caller-supplied anchor would let anyone graft spans onto an unrelated trace.
    /// </summary>
    public string? TraceRoot { get; init; }

    /// <summary>The enclosing lane's anchor.</summary>
    public string? ParentTraceRoot { get; init; }

    /// <summary>
    /// Episode start. These four episode fields are always copied together — a carrier that copies
    /// some of them degrades its consumer to a partial activation span covering only its own hop.
    /// </summary>
    public DateTimeOffset? EpisodeStartedAt { get; init; }

    /// <summary>What opened the episode.</summary>
    public string? EpisodeTrigger { get; init; }

    /// <summary>The transition the episode was triggered with.</summary>
    public string? EpisodeTransitionKey { get; init; }

    /// <summary>The trace root under which the episode began.</summary>
    public string? EpisodeTraceRoot { get; init; }
}
