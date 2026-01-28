using BBT.Aether.DependencyInjection;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Handles InstanceFaultedCleanupEvent to cancel all scheduled jobs when instance faults.
/// This handler delegates the actual cancellation logic to IInstanceCancellationService.
/// </summary>
/// <remarks>
/// Registered in Inbox worker to process cleanup events from the event bus.
/// Works in conjunction with InstanceFaultedCleanupEventHook for local and remote processing.
/// </remarks>
internal sealed class InstanceFaultedCleanupEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceFaultedCleanupEventHandler> logger) : IEventHandler<InstanceFaultedCleanupEvent>
{
    /// <summary>
    /// Handles the InstanceFaultedCleanupEvent by delegating cancellation cleanup to the service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceFaultedCleanupEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.InstanceFaultedCleanupEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.InstanceId,
                eventData.Flow);
            return;
        }

        logger.InstanceFaultedCleanupEventReceived(
            eventData.InstanceId,
            eventData.Flow);

        using (currentSchema.Use(eventData.Flow))
        {
            await scopeFactory.ExecuteInNewScopeAsync(async sp =>
            {
                var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                var cancellationService = sp.GetRequiredService<IInstanceCancellationService>();

                await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, cancellationToken);

                var result = await cancellationService.ProcessCancellationAsync(
                    eventData.InstanceId,
                    cancellationToken);

                await uow.CommitAsync(cancellationToken);

                if (!result.IsSuccess)
                {
                    logger.InstanceFaultedCleanupProcessingFailed(
                        new InvalidOperationException(result.Error.Message),
                        eventData.InstanceId);
                }
                else
                {
                    logger.InstanceFaultedCleanupSucceeded(
                        eventData.InstanceId);
                }
            });
        }
    }
}
