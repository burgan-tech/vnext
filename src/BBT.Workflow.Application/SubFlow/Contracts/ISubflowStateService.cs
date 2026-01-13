namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for handling SubFlow state change synchronization with parent instances.
/// Updates parent's InstanceCorrelation and EffectiveState when SubFlow state changes.
/// </summary>
public interface ISubflowStateService
{
    /// <summary>
    /// Updates the parent instance with the SubFlow's new state.
    /// Updates both the correlation's SubFlowCurrentState and the parent's EffectiveState.
    /// </summary>
    /// <param name="input">The SubFlow state change input containing parent and state information.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpdateParentStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default);
}
