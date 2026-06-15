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

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = eventData.Domain,
            [TelemetryConstants.TagNames.Flow] = eventData.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = eventData.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = eventData.InstanceId,
        }))
        {
            logger.InstanceCanceledEventReceived(
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
                        logger.InstanceCanceledProcessingFailed(
                            new InvalidOperationException(result.Error.Message),
                            eventData.InstanceId);
                    }
                }, cancellationToken);
            }
        }
    }
}
