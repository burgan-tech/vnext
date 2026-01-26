using BBT.Aether.Application.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowStateService" />
public sealed class SubflowStateService(
    IServiceProvider serviceProvider,
    IInstanceRepository instanceRepository,
    ILogger<SubflowStateService> logger)
    : ApplicationService(serviceProvider), ISubflowStateService
{
    /// <inheritdoc />
    public async Task UpdateParentStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        logger.SubFlowStateChangeReceived(
            input.SubInstanceId,
            input.ParentInstanceId,
            input.NewState);

        var parentInstance = await instanceRepository.FindAsync(
            input.ParentInstanceId, true, cancellationToken);

        if (parentInstance == null)
        {
            logger.LogWarning(
                "Parent instance {ParentInstanceId} not found for SubFlow state change from {SubInstanceId}",
                input.ParentInstanceId,
                input.SubInstanceId);
            return;
        }

        var correlation = parentInstance.FindCorrelationBySubInstanceId(input.SubInstanceId);
        if (correlation == null)
        {
            logger.LogWarning(
                "Correlation not found for SubInstance {SubInstanceId} in parent {ParentInstanceId}",
                input.SubInstanceId,
                input.ParentInstanceId);
            return;
        }

        if (correlation.IsCompleted)
        {
            logger.LogDebug(
                "Correlation for SubInstance {SubInstanceId} is already completed, skipping state update",
                input.SubInstanceId);
            return;
        }
        
        // Out-of-order event detection using timestamp:
        // If correlation already has a state update with a later timestamp,
        // this event is out-of-order/stale - reject it to prevent downgrade
        if (correlation.SubFlowStateChangedAt.HasValue && 
            input.ChangedAt < correlation.SubFlowStateChangedAt.Value)
        {
            logger.LogWarning(
                "Rejecting out-of-order SubFlow state event for {SubInstanceId}. " +
                "Event timestamp {EventTime} is older than correlation's last update {CorrelationTime}. " +
                "Correlation state: '{CorrelationState}', Event state: '{EventState}'.",
                input.SubInstanceId,
                input.ChangedAt,
                correlation.SubFlowStateChangedAt.Value,
                correlation.SubFlowCurrentState,
                input.NewState);
            return;
        }

        // Update correlation's SubFlowCurrentState with timestamp
        correlation.UpdateSubFlowState(input.NewState, input.ChangedAt);

        // Propagate EffectiveState with type and subtype to parent (and recursively upward if parent is also a SubFlow)
        parentInstance.PropagateEffectiveStateToParent(input.NewState, input.NewStateType, input.NewStateSubType);

        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);

        logger.SubFlowStateChangeApplied(
            input.SubInstanceId,
            input.ParentInstanceId,
            input.NewState);
    }
}
