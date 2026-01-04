using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for handling child subflow cancellation operations.
/// Propagates cancellation requests to child subflows.
/// Uses IInstanceCommandGateway to route between local and remote execution based on target domain.
/// </summary>
/// <remarks>
/// This service encapsulates the business logic for child subflow cancellation,
/// making it reusable across different consumers (handlers, hooks, controllers).
/// </remarks>
public sealed class ChildSubflowCancellationService(
    IServiceProvider serviceProvider,
    IInstanceCommandGateway instanceCommandGateway,
    ILogger<ChildSubflowCancellationService> logger)
    : ApplicationService(serviceProvider), IChildSubflowCancellationService
{
    /// <inheritdoc />
    public async Task<Result> CancelChildSubflowAsync(
        Guid instanceId,
        string domain,
        string flow,
        string? version,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await instanceCommandGateway.TransitionAsync(
                instanceId,
                WellKnownTransitionKeys.Cancel,
                new Instances.TransitionInput(
                    domain: domain,
                    workflow: flow,
                    version: version),
                cancellationToken: cancellationToken);

            if (result.IsSuccess)
            {
                logger.ChildSubflowCancelSucceeded(instanceId);
                return Result.Ok();
            }

            logger.ChildSubflowCancelFailed(instanceId);
            return Result.Fail(WorkflowErrors.ChildSubflowCancellationFailed(instanceId, "Transition failed"));
        }
        catch (Exception ex)
        {
            logger.ChildSubflowCancelError(ex, instanceId);
            return Result.Fail(WorkflowErrors.ChildSubflowCancellationFailed(instanceId, ex.Message));
        }
    }
}

