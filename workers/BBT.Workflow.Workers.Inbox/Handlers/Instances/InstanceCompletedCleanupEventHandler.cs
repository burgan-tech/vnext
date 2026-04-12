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
/// Handles InstanceCompletedCleanupEvent to cancel all scheduled jobs when instance completes.
/// This handler delegates the actual cancellation logic to IInstanceCancellationService.
/// </summary>
/// <remarks>
/// Registered in Inbox worker to process cleanup events from the event bus.
/// Works in conjunction with InstanceCompletedCleanupEventHook for local and remote processing.
/// </remarks>
internal sealed class InstanceCompletedCleanupEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceCompletedCleanupEventHandler> logger) : IEventHandler<InstanceCompletedCleanupEvent>
{
    /// <summary>
    /// Handles the InstanceCompletedCleanupEvent by delegating cancellation cleanup to the service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceCompletedCleanupEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.InstanceCompletedCleanupEventIgnoredDomainMismatch(
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
            logger.InstanceCompletedCleanupEventReceived(
                eventData.InstanceId,
                eventData.Flow);

            using (currentSchema.Use(eventData.Flow))
            {
                await scopeFactory.ExecuteWithWorkflowAsync(eventData.Domain, eventData.Flow, eventData.Version,
                    async (sp, ct) =>
                    {
                        var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                        var cancellationService = sp.GetRequiredService<IInstanceCancellationService>();

                        await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                        {
                            Scope = UnitOfWorkScopeOption.RequiresNew
                        }, ct);

                        var result = await cancellationService.ProcessCancellationAsync(
                            eventData.InstanceId,
                            ct);

                        await uow.CommitAsync(ct);

                        if (!result.IsSuccess)
                        {
                            logger.InstanceCompletedCleanupProcessingFailed(
                                new InvalidOperationException(result.Error.Message),
                                eventData.InstanceId);
                        }
                        else
                        {
                            logger.InstanceCompletedCleanupSucceeded(
                                eventData.InstanceId);
                        }
                    }, cancellationToken);
            }
        }
    }
}