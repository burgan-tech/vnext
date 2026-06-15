using BBT.Aether.DependencyInjection;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Handles the InstanceSubFaultedEvent to propagate SubFlow fault to the parent instance.
/// This handler delegates the actual fault processing to ISubflowFaultService.
/// </summary>
internal sealed class InstanceSubFaultedEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceSubFaultedEventHandler> logger) : IEventHandler<InstanceSubFaultedEvent>
{
    /// <summary>
    /// Handles the InstanceSubFaultedEvent by delegating to the subflow fault service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubFaultedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowFaultIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.SubInstanceId,
                eventData.InstanceId);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = eventData.SubInstanceId,
        }))
        {
            logger.SubFlowFaultReceived(
                eventData.SubInstanceId,
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            using (currentSchema.Use(eventData.Flow))
            {
                var faultedData = new SubFlowFaultedInput
                {
                    SubInstanceId = eventData.SubInstanceId,
                    InstanceId = eventData.InstanceId,
                    Domain = eventData.Domain,
                    Flow = eventData.Flow,
                    Version = eventData.Version,
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

                await scopeFactory.ExecuteWithWorkflowAsync(eventData.Domain, eventData.Flow, eventData.Version,
                    async (sp, ct) =>
                    {
                        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                        var subflowFaultService = sp.GetRequiredService<ISubflowFaultService>();
                        await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                        {
                            Scope = UnitOfWorkScopeOption.RequiresNew
                        }, ct);

                        await subflowFaultService.FaultAsync(faultedData, ct);
                        await uow.CommitAsync(ct);
                    }, cancellationToken);
            }
        }
    }
}
