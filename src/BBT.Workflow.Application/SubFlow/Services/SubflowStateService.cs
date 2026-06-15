using System.Diagnostics;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowStateService" />
public sealed class SubflowStateService(
    IInstanceRepository instanceRepository,
    ILogger<SubflowStateService> logger)
    :  ISubflowStateService
{
    /// <inheritdoc />
    public async Task UpdateParentStateAsync(
        SubFlowStateChangedInput input,
        CancellationToken cancellationToken = default)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.StateChange/{input.Domain}/{input.Flow}");
        SubFlowActivityHelper.EnrichWithStateChange(
            activity,
            input.SubInstanceId,
            input.ParentInstanceId,
            input.Domain,
            input.Flow,
            input.NewState);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = input.Domain,
            [TelemetryConstants.TagNames.Flow] = input.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = input.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = input.ParentInstanceId,
            [TelemetryConstants.TagNames.ParentInstanceId] = input.ParentInstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = input.SubInstanceId
        }))
        {
            try
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
                    activity?.SetTag("vnext.subflow.result", "parent_not_found");
                    return;
                }

                var correlation = parentInstance.FindCorrelationBySubInstanceId(input.SubInstanceId);
                if (correlation == null)
                {
                    logger.LogWarning(
                        "Correlation not found for SubInstance {SubInstanceId} in parent {ParentInstanceId}",
                        input.SubInstanceId,
                        input.ParentInstanceId);
                    activity?.SetTag("vnext.subflow.result", "correlation_not_found");
                    return;
                }

                if (correlation.IsCompleted)
                {
                    logger.LogDebug(
                        "Correlation for SubInstance {SubInstanceId} is already completed, skipping state update",
                        input.SubInstanceId);
                    activity?.SetTag("vnext.subflow.result", "already_completed");
                    return;
                }

                // Out-of-order event detection using timestamp:
                // If correlation already has a state update with a later timestamp,
                // this event is out-of-order/stale - reject it to prevent downgrade.
                // Both sides are truncated to microseconds to avoid false rejections caused
                // by precision loss when DateTime (100ns ticks) is stored in PostgreSQL (microseconds).
                if (correlation.SubFlowStateChangedAt.HasValue &&
                    TruncateToMicroseconds(input.ChangedAt) < TruncateToMicroseconds(correlation.SubFlowStateChangedAt.Value))
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
                    activity?.SetTag("vnext.subflow.result", "out_of_order");
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

                SubFlowActivityHelper.SetSuccess(activity);
            }
            catch (Exception ex)
            {
                SubFlowActivityHelper.SetError(activity, ex.Message, ex);
                throw;
            }
        }
    }

    // Truncates a DateTime to microsecond precision (removes sub-microsecond ticks).
    // PostgreSQL stores timestamps with microsecond precision; .NET DateTime uses 100ns ticks.
    // Without truncation, a stored-then-read value may differ by up to 900ns from the original,
    // causing valid equal-timestamp events to appear out-of-order.
    private static DateTime TruncateToMicroseconds(DateTime dt)
        => new(dt.Ticks - dt.Ticks % 10, dt.Kind);
}
