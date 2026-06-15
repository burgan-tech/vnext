using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Hook executed before InstanceSubCompletedEvent is published.
/// Performs pre-publish processing for sub-flow completion events.
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
/// <remarks>
/// Register this hook in DI using:
/// <code>
/// services.AddEventHook&lt;InstanceSubCompletedEvent, InstanceSubCompletedEventHook&gt;();
/// </code>
/// </remarks>
public sealed class InstanceSubCompletedEventHook(
    ILogger<InstanceSubCompletedEventHook> logger,
    IInstanceCommandGateway instanceCommandGateway) : IEventPublishHook<InstanceSubCompletedEvent>
{
    /// <summary>
    /// Executes hook logic before the InstanceSubCompletedEvent is published.
    /// Delegates to IInstanceCommandGateway which handles local/remote routing automatically.
    /// </summary>
    /// <param name="eventData">The strongly-typed event data.</param>
    /// <param name="context">The hook context with metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hook execution result.</returns>
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceSubCompletedEvent eventData,
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
            logger.SubFlowEventReceived(eventData.SubInstanceId, eventData.InstanceId, eventData.Domain, eventData.Flow);

            try
            {
                var input = MapToFlowCompletedInput(eventData);

                // Gateway handles local/remote routing based on domain
                var result = await instanceCommandGateway.CompleteAsync(input, cancellationToken);

                if (!result.IsSuccess)
                {
                    logger.SubFlowCompletionFailed(
                        new Exception(result.Error.Message),
                        eventData.SubInstanceId,
                        eventData.InstanceId);

                    return EventHookResult.Fail(
                        new Exception(result.Error.Message),
                        new Dictionary<string, string>
                        {
                            ["hook_error"] = "SubFlowCompletionFailed",
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
                logger.SubFlowCompletionFailed(ex, eventData.SubInstanceId, eventData.InstanceId);

                return EventHookResult.Fail(ex, new Dictionary<string, string>
                {
                    ["hook_error"] = "SubFlowCompletionHookFailed"
                });
            }
        }
    }

    /// <summary>
    /// Maps the event data to FlowCompletedInput DTO.
    /// </summary>
    private static FlowCompletedInput MapToFlowCompletedInput(InstanceSubCompletedEvent eventData)
    {
        return new FlowCompletedInput
        {
            InstanceId = eventData.InstanceId,
            Domain = eventData.Domain,
            Flow = eventData.Flow,
            CompletedAt = eventData.CompletedAt,
            CompletedState = eventData.CompletedState,
            Duration = eventData.Duration,
            SubInstanceId = eventData.SubInstanceId,
            InstanceData = eventData.InstanceData,
            Version = eventData.Version
        };
    }
}
