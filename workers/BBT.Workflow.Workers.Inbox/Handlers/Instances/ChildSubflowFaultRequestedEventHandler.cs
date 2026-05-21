using BBT.Aether.DependencyInjection;
using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Handles ChildSubflowFaultRequestedEvent to propagate fault downward to child SubFlow instances.
/// When a parent faults, this handler loads the child instance and faults it, which recursively
/// triggers further downward and upward propagation through the child's own Fault() logic.
/// </summary>
internal sealed class ChildSubflowFaultRequestedEventHandler(
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<ChildSubflowFaultRequestedEventHandler> logger
) : IEventHandler<ChildSubflowFaultRequestedEvent>
{
    /// <summary>
    /// Handles the ChildSubflowFaultRequestedEvent by loading and faulting the child instance.
    /// Idempotent: skips if the child is already Faulted or Completed.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<ChildSubflowFaultRequestedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.ChildSubflowFaultIgnoredDomainMismatch(
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
            logger.ChildSubflowFaultRequestReceived(
                eventData.InstanceId,
                eventData.Domain,
                eventData.Flow);

            await scopeFactory.ExecuteWithWorkflowAsync(eventData.Domain, eventData.Flow, eventData.Version,
                async (sp, ct) =>
                {
                    var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                    var instanceRepository = sp.GetRequiredService<IInstanceRepository>();

                    await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
                    {
                        Scope = UnitOfWorkScopeOption.RequiresNew
                    }, ct);

                    var childInstance = await instanceRepository.FindAsync(
                        eventData.InstanceId, true, ct);

                    if (childInstance == null)
                    {
                        logger.InstanceNotFound(eventData.InstanceId, eventData.Flow);
                        return;
                    }

                    // Idempotency: skip if child is already in a terminal state
                    if (childInstance.Status.Equals(InstanceStatus.Faulted) ||
                        childInstance.Status.Equals(InstanceStatus.Completed))
                    {
                        return;
                    }

                    childInstance.Fault(eventData.Domain);
                    await instanceRepository.UpdateAsync(childInstance, true, ct);

                    logger.ChildSubflowFaultApplied(eventData.InstanceId, eventData.ParentInstanceId);

                    await uow.CommitAsync(ct);
                }, cancellationToken);
        }
    }
}
