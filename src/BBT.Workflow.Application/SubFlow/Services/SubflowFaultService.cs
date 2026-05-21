using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowFaultService" />
public sealed class SubflowFaultService(
    IUnitOfWorkManager uowManager,
    IInstanceRepository instanceRepository,
    ILogger<SubflowFaultService> logger)
    : ISubflowFaultService
{
    /// <inheritdoc />
    public async Task FaultAsync(
        SubFlowFaultedInput input,
        CancellationToken cancellationToken = default)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Fault/{input.Domain}/{input.Flow}");
        SubFlowActivityHelper.EnrichWithCompletion(
            activity,
            input.SubInstanceId,
            input.InstanceId,
            input.Domain,
            input.Flow);

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = input.Domain,
            [TelemetryConstants.TagNames.Flow] = input.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = input.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = input.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = input.SubInstanceId
        }))
        {
            try
            {
                await using var uow = await uowManager.BeginAsync(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
                    cancellationToken);

                var parentInstance = await instanceRepository.FindAsync(
                    input.InstanceId, true, cancellationToken);

                if (parentInstance == null)
                {
                    logger.InstanceNotFound(input.InstanceId, input.Flow);
                    activity?.SetTag("vnext.subflow.result", "parent_not_found");
                    return;
                }

                // Idempotency: skip if parent is already in a terminal state
                if (parentInstance.Status.Equals(InstanceStatus.Faulted) ||
                    parentInstance.Status.Equals(InstanceStatus.Completed))
                {
                    activity?.SetTag("vnext.subflow.result", "parent_already_terminal");
                    return;
                }

                // Verify the correlation exists and is still open
                var correlation = parentInstance.FindCorrelationBySubInstanceId(input.SubInstanceId);
                if (correlation == null || correlation.IsCompleted)
                {
                    logger.SubFlowCorrelationNotFound(input.SubInstanceId);
                    activity?.SetTag("vnext.subflow.result", "correlation_not_found_or_completed");
                    return;
                }

                // Record the child's incident on the parent with "SubFlow" error layer
                var incident = InstanceIncidentFactory.Create(
                    state: parentInstance.GetCurrentState,
                    transition: string.Empty,
                    taskKey: input.IncidentTaskKey,
                    message: $"SubFlow '{input.SubFlowName ?? input.Flow}' faulted: {input.IncidentMessage ?? "Unknown error"}",
                    errorCode: $"SubFlow:Faulted:{input.IncidentErrorCode ?? "Unknown"}",
                    errorLayer: "SubFlow",
                    stackTrace: null,
                    boundaryAction: input.IncidentBoundaryAction,
                    boundaryLevel: input.IncidentBoundaryLevel,
                    traceId: input.IncidentTraceId);

                parentInstance.AddIncident(incident);
                parentInstance.Fault(input.Domain);

                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
                await uow.CommitAsync(cancellationToken);

                logger.SubFlowFaultPropagatedToParent(input.SubInstanceId, input.InstanceId);
                SubFlowActivityHelper.SetSuccess(activity);
            }
            catch (Exception ex)
            {
                SubFlowActivityHelper.SetError(activity, ex.Message, ex);
                logger.SubFlowFaultProcessingFailed(ex, input.SubInstanceId, input.InstanceId);
                throw;
            }
        }
    }
}
