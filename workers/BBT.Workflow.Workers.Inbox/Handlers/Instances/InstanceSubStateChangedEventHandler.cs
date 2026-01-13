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
/// Handles the InstanceSubStateChangedEvent to process SubFlow state changes.
/// This handler delegates the actual state update logic to ISubflowStateService.
/// Used as fallback when the event hook fails and the event is processed via outbox/inbox.
/// </summary>
internal sealed class InstanceSubStateChangedEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceSubStateChangedEventHandler> logger) : IEventHandler<InstanceSubStateChangedEvent>
{
    /// <summary>
    /// Handles the InstanceSubStateChangedEvent by delegating to the subflow state service.
    /// Updates the parent instance's EffectiveState and correlation's SubFlowCurrentState.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubStateChangedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.SubInstanceId,
                eventData.ParentInstanceId);
            return;
        }

        logger.SubFlowStateChangedEventReceived(
            eventData.SubInstanceId,
            eventData.ParentInstanceId,
            eventData.NewState);

        using (currentSchema.Use(eventData.Flow))
        {
            var input = new SubFlowStateChangedInput
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

            await scopeFactory.ExecuteInNewScopeAsync(async sp =>
            {
                var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                var subflowStateService = sp.GetRequiredService<ISubflowStateService>();

                await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, cancellationToken);

                await subflowStateService.UpdateParentStateAsync(input, cancellationToken);
                await uow.CommitAsync(cancellationToken);
            });
        }
    }
}
