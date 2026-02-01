using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for forwarding transitions to active subflow instances.
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
public sealed class SubflowForwardingService(
    IInstanceCommandGateway instanceCommandGateway)
    : ISubflowForwardingService
{
    /// <inheritdoc />
    public async Task<Result<TransitionOutput>> ForwardTransitionAsync(
        Guid instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken ct)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Forward/{input.Domain}/{input.Workflow}/{transitionKey}");
        SubFlowActivityHelper.EnrichWithForward(activity, instanceId, transitionKey);

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