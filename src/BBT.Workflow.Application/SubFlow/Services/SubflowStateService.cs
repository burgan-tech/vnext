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

        // Update correlation's SubFlowCurrentState
        correlation.UpdateSubFlowState(input.NewState);

        // Update parent's EffectiveState with SubFlow's state
        parentInstance.SetEffectiveState(input.NewState);

        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);

        logger.SubFlowStateChangeApplied(
            input.SubInstanceId,
            input.ParentInstanceId,
            input.NewState);
    }
}
