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
/// Handles the InstanceSubCompletedEvent to process SubFlow/SubProcess completion.
/// This handler delegates the actual completion logic to ISubflowCompletionService.
/// </summary>
internal sealed class InstanceSubCompletedEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceSubCompletedEventHandler> logger) : IEventHandler<InstanceSubCompletedEvent>
{
    /// <summary>
    /// Handles the InstanceSubCompletedEvent by delegating to the subflow completion service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubCompletedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowEventIgnoredDomainMismatch(
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
            logger.SubFlowEventReceived(
                eventData.SubInstanceId,
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            using (currentSchema.Use(eventData.Flow))
            {
                var completedData = new FlowCompletedInput
                {
                    SubInstanceId = eventData.SubInstanceId,
                    InstanceId = eventData.InstanceId,
                    Domain = eventData.Domain,
                    Flow = eventData.Flow,
                    Version = eventData.Version,
                    CompletedState = eventData.CompletedState,
                    InstanceData = eventData.InstanceData,
                    CompletedAt = eventData.CompletedAt,
                    Duration = eventData.Duration
                };

                await scopeFactory.ExecuteWithWorkflowAsync(eventData.Domain, eventData.Flow, eventData.Version,
                    async (sp, ct) =>
                    {
                        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                        var subflowCompletionService = sp.GetRequiredService<ISubflowCompletionService>();
                        await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                        {
                            Scope = UnitOfWorkScopeOption.RequiresNew
                        }, ct);

                        await subflowCompletionService.CompletionAsync(completedData, ct);
                        await uow.CommitAsync(ct);
                    }, cancellationToken);
            }
        }
    }
}