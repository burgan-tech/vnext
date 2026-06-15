using BBT.Aether.DependencyInjection;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Handles ChildSubflowCancelRequestedEvent to propagate cancellation to child subflows.
/// This handler delegates the actual cancellation logic to IChildSubflowCancellationService.
/// </summary>
internal sealed class ChildSubflowCancelRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<ChildSubflowCancelRequestedEventHandler> logger
) : IEventHandler<ChildSubflowCancelRequestedEvent>
{
    /// <summary>
    /// Handles the ChildSubflowCancelRequestedEvent by delegating to the cancellation service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<ChildSubflowCancelRequestedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.ChildSubflowCancelEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.InstanceId,
                eventData.Flow);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
        }))
        {
            logger.ChildSubflowCancelRequestReceived(
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            await scopeFactory.ExecuteWithWorkflowAsync(eventData.Domain, eventData.Flow, eventData.Version,
                async (sp, ct) =>
                {
                    var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                    var cancellationService = sp.GetRequiredService<IChildSubflowCancellationService>();

                    await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                    {
                        Scope = UnitOfWorkScopeOption.RequiresNew
                    }, ct);

                    await cancellationService.CancelChildSubflowAsync(
                        eventData.InstanceId,
                        eventData.Domain,
                        eventData.Flow,
                        eventData.Version,
                        ct);

                    await uow.CommitAsync(ct);
                }, cancellationToken);
        }
    }
}