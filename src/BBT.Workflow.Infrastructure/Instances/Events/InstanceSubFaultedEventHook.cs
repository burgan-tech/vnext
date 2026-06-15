using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Hook executed before InstanceSubFaultedEvent is published.
/// Propagates SubFlow fault to parent instance via IInstanceCommandGateway.
/// </summary>
/// <remarks>
/// Register this hook in DI using:
/// <code>
/// services.AddEventHook&lt;InstanceSubFaultedEvent, InstanceSubFaultedEventHook&gt;();
/// </code>
/// </remarks>
public sealed class InstanceSubFaultedEventHook(
    ILogger<InstanceSubFaultedEventHook> logger,
    IInstanceCommandGateway instanceCommandGateway) : IEventPublishHook<InstanceSubFaultedEvent>
{
    /// <summary>
    /// Executes hook logic before the InstanceSubFaultedEvent is published.
    /// Delegates to IInstanceCommandGateway which handles local/remote routing automatically.
    /// </summary>
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceSubFaultedEvent eventData,
        EventHookContext context,
        CancellationToken cancellationToken = default)
    {
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId,
        }))
        {
            logger.SubFlowFaultReceived(eventData.SubInstanceId, eventData.InstanceId, eventData.Domain, eventData.Flow);

            try
            {
                var input = MapToSubFlowFaultedInput(eventData);

                var result = await instanceCommandGateway.FaultAsync(input, cancellationToken);

                if (!result.IsSuccess)
                {
                    logger.SubFlowFaultProcessingFailed(
                        new Exception(result.Error.Message),
                        eventData.SubInstanceId,
                        eventData.InstanceId);

                    return EventHookResult.Fail(
                        new Exception(result.Error.Message),
                        new Dictionary<string, string>
                        {
                            ["hook_error"] = "SubFlowFaultPropagationFailed",
                            ["error_code"] = result.Error.Code ?? "unknown"
                        });
                }

                return EventHookResult.Ok(new Dictionary<string, string>
                {
                    ["hook_executed"] = "true",
                    ["sub_instance_id"] = eventData.SubInstanceId.ToString(),
                    ["parent_instance_id"] = eventData.InstanceId.ToString()
                });
            }
            catch (Exception ex)
            {
                logger.SubFlowFaultProcessingFailed(ex, eventData.SubInstanceId, eventData.InstanceId);

                return EventHookResult.Fail(ex, new Dictionary<string, string>
                {
                    ["hook_error"] = "SubFlowFaultHookFailed"
                });
            }
        }
    }

    private static SubFlowFaultedInput MapToSubFlowFaultedInput(InstanceSubFaultedEvent eventData)
    {
        return new SubFlowFaultedInput
        {
            InstanceId = eventData.InstanceId,
            Domain = eventData.Domain,
            Flow = eventData.Flow,
            Version = eventData.Version,
            SubInstanceId = eventData.SubInstanceId,
            FaultedState = eventData.FaultedState,
            FaultedStateType = eventData.FaultedStateType,
            FaultedStateSubType = eventData.FaultedStateSubType,
            InstanceData = eventData.InstanceData,
            FaultedAt = eventData.FaultedAt,
            SubFlowName = eventData.SubFlowName,
            IncidentMessage = eventData.IncidentMessage,
            IncidentErrorCode = eventData.IncidentErrorCode,
            IncidentErrorLayer = eventData.IncidentErrorLayer,
            IncidentStatusCode = eventData.IncidentStatusCode,
            IncidentTraceId = eventData.IncidentTraceId,
            IncidentTaskKey = eventData.IncidentTaskKey,
            IncidentTransition = eventData.IncidentTransition,
            IncidentState = eventData.IncidentState,
            IncidentBoundaryAction = eventData.IncidentBoundaryAction,
            IncidentBoundaryLevel = eventData.IncidentBoundaryLevel
        };
    }
}
