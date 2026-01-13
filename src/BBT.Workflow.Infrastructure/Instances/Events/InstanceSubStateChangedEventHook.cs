using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Hook executed before InstanceSubStateChangedEvent is published.
/// Performs pre-publish processing to update parent instance with SubFlow's state change.
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
/// <remarks>
/// Register this hook in DI using:
/// <code>
/// services.AddEventHook&lt;InstanceSubStateChangedEvent, InstanceSubStateChangedEventHook&gt;();
/// </code>
/// </remarks>
public sealed class InstanceSubStateChangedEventHook(
    ILogger<InstanceSubStateChangedEventHook> logger,
    IInstanceCommandGateway instanceCommandGateway) : IEventPublishHook<InstanceSubStateChangedEvent>
{
    /// <summary>
    /// Executes hook logic before the InstanceSubStateChangedEvent is published.
    /// Delegates to IInstanceCommandGateway which handles local/remote routing automatically.
    /// </summary>
    /// <param name="eventData">The strongly-typed event data.</param>
    /// <param name="context">The hook context with metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hook execution result.</returns>
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceSubStateChangedEvent eventData,
        EventHookContext context,
        CancellationToken cancellationToken = default)
    {
        logger.SubFlowStateChangedEventReceived(
            eventData.SubInstanceId,
            eventData.ParentInstanceId,
            eventData.NewState);

        try
        {
            var input = MapToSubFlowStateChangedInput(eventData);

            // Gateway handles local/remote routing based on domain
            var result = await instanceCommandGateway.UpdateSubFlowStateAsync(input, cancellationToken);

            if (!result.IsSuccess)
            {
                var error = result.Error;
                
                // Log with structured error details
                logger.LogWarning(
                    "SubFlow state update failed for SubInstance {SubInstanceId}, Parent {ParentInstanceId}. " +
                    "Error: [{ErrorCode}] {ErrorMessage}",
                    eventData.SubInstanceId,
                    eventData.ParentInstanceId,
                    error.Code ?? "unknown",
                    error.Message);

                // Preserve error context in metadata for diagnostics
                var metadata = new Dictionary<string, string>
                {
                    ["hook_error"] = "SubFlowStateUpdateFailed",
                    ["error_code"] = error.Code ?? "unknown",
                    ["error_prefix"] = error.Prefix ?? "unknown",
                    ["error_message"] = error.Message
                };
                
                if (!string.IsNullOrEmpty(error.Target))
                {
                    metadata["error_target"] = error.Target;
                }

                return EventHookResult.Fail(
                    new InvalidOperationException($"[{error.Code}] {error.Message}"),
                    metadata);
            }

            return EventHookResult.Ok(new Dictionary<string, string>
            {
                ["hook_executed"] = "true",
                ["sub_instance_id"] = eventData.SubInstanceId.ToString(),
                ["parent_instance_id"] = eventData.ParentInstanceId.ToString(),
                ["new_state"] = eventData.NewState
            });
        }
        catch (Exception ex)
        {
            logger.SubFlowStateUpdateFailed(ex, eventData.SubInstanceId, eventData.ParentInstanceId);

            return EventHookResult.Fail(ex, new Dictionary<string, string>
            {
                ["hook_error"] = "SubFlowStateChangedHookFailed"
            });
        }
    }

    /// <summary>
    /// Maps the event data to SubFlowStateChangedInput DTO.
    /// </summary>
    private static SubFlowStateChangedInput MapToSubFlowStateChangedInput(InstanceSubStateChangedEvent eventData)
    {
        return new SubFlowStateChangedInput
        {
            ParentInstanceId = eventData.ParentInstanceId,
            SubInstanceId = eventData.SubInstanceId,
            Domain = eventData.Domain,
            Flow = eventData.Flow,
            Version = eventData.Version,
            NewState = eventData.NewState,
            PreviousState = eventData.PreviousState,
            ChangedAt = eventData.ChangedAt
        };
    }
}
