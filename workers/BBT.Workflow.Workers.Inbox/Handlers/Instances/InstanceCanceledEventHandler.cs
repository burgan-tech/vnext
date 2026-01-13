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
/// Handles InstanceCanceledEvent to propagate cancellation to child flows and cancel active jobs.
/// This handler delegates the actual cancellation logic to IInstanceCancellationService.
/// </summary>
internal sealed class InstanceCanceledEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceCanceledEventHandler> logger) : IEventHandler<InstanceCanceledEvent>
{
    /// <summary>
    /// Handles the InstanceCanceledEvent by delegating cancellation cleanup to the service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceCanceledEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.InstanceCanceledEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.InstanceId,
                eventData.Flow);
            return;
        }

        logger.InstanceCanceledEventReceived(
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
                    logger.InstanceCanceledProcessingFailed(
                        new InvalidOperationException(result.Error.Message),
                        eventData.InstanceId);
                }
            });
        }
    }
}
