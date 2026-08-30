using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Propagates a canceled SubItem outcome to its parent before the event is published.
/// </summary>
public sealed class InstanceSubCanceledEventHook(
    ILogger<InstanceSubCanceledEventHook> logger,
    IInstanceCommandGateway instanceCommandGateway) : IEventPublishHook<InstanceSubCanceledEvent>
{
    /// <inheritdoc />
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceSubCanceledEvent eventData,
        EventHookContext context,
        CancellationToken cancellationToken = default)
    {
        var scopeProps = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.RootInstanceId] = eventData.RootInstanceId?.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.ParentInstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId,
            [TelemetryConstants.TagNames.SubItemType] = eventData.SubItemType.ToString(),
            [TelemetryConstants.TagNames.SubItemOutcome] = "Canceled",
            [TelemetryConstants.TagNames.TerminationOrigin] = eventData.TerminationOrigin.ToString(),
            [TelemetryConstants.TagNames.TerminationInitiator] = eventData.InitiatorInstanceId.ToString(),
            [TelemetryConstants.TagNames.TerminationCascadeId] = eventData.CascadeId.ToString()
        };

        using (logger.BeginScope(scopeProps))
        {
            logger.SubFlowEventReceived(
                eventData.SubInstanceId,
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            try
            {
                var result = await instanceCommandGateway.CancelAsync(Map(eventData), cancellationToken);
                if (!result.IsSuccess)
                {
                    var exception = new Exception(result.Error.Message);
                    logger.LogError(
                        exception,
                        "Canceled SubItem propagation failed for child {SubInstanceId}, parent {ParentInstanceId}",
                        eventData.SubInstanceId,
                        eventData.InstanceId);
                    return EventHookResult.Fail(exception, new Dictionary<string, string>
                    {
                        ["hook_error"] = "SubItemCancellationPropagationFailed",
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
                logger.LogError(
                    ex,
                    "Canceled SubItem hook failed for child {SubInstanceId}, parent {ParentInstanceId}",
                    eventData.SubInstanceId,
                    eventData.InstanceId);
                return EventHookResult.Fail(ex, new Dictionary<string, string>
                {
                    ["hook_error"] = "SubItemCancellationHookFailed"
                });
            }
        }
    }

    private static SubItemCanceledInput Map(InstanceSubCanceledEvent eventData) => new()
    {
        InstanceId = eventData.InstanceId,
        SubInstanceId = eventData.SubInstanceId,
        Domain = eventData.Domain,
        Flow = eventData.Flow,
        Version = eventData.Version,
        CanceledState = eventData.CanceledState,
        CanceledAt = eventData.CanceledAt,
        RootInstanceId = eventData.RootInstanceId,
        Sync = eventData.Sync,
        Termination = new TerminationContext(
            eventData.TerminationOrigin,
            eventData.InitiatorInstanceId,
            eventData.CascadeId),
        RearmAttempt = eventData.RearmAttempt
    };
}
