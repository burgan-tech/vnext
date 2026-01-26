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
}
