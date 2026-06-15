using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for forwarding transitions to active subflow instances.
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
public sealed class SubflowForwardingService(
    IInstanceCommandGateway instanceCommandGateway,
    ILogger<SubflowForwardingService> logger)
    : ISubflowForwardingService
{
    /// <inheritdoc />
    public async Task<Result<TransitionOutput>> ForwardTransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken ct,
        Guid? parentInstanceId = null)
    {
        var scopeData = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = input.Domain,
            [TelemetryConstants.TagNames.Flow] = input.Workflow,
            [TelemetryConstants.TagNames.InstanceId] = instanceId,
            [TelemetryConstants.TagNames.TransitionKey] = transitionKey
        };
        if (parentInstanceId.HasValue)
            scopeData[TelemetryConstants.TagNames.ParentInstanceId] = parentInstanceId.Value;

        using (logger.BeginScope(scopeData))
        {
            using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Forward/{input.Domain}/{input.Workflow}/{transitionKey}");
            SubFlowActivityHelper.EnrichWithForward(activity, instanceId, transitionKey, parentInstanceId);

            var result = await instanceCommandGateway
                .TransitionAsync(
                    instanceId,
                    transitionKey,
                    input,
                    ct
                );

            if (result.IsSuccess)
            {
                activity?.SetTag("vnext.subflow.forward.result", "success");
                activity?.SetTag("vnext.subflow.forward.status", result.Value!.Status.ToString());
                SubFlowActivityHelper.SetSuccess(activity);
            }
            else
            {
                activity?.SetTag("vnext.subflow.forward.result", "failed");
                SubFlowActivityHelper.SetError(activity, result.Error.Message);
            }

            return result;
        }
    }
}