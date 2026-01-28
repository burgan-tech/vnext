using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Hook executed before InstanceFaultedCleanupEvent is published.
/// Cancels all scheduled jobs for the faulted instance.
/// </summary>
/// <remarks>
/// Register this hook in DI using:
/// <code>
/// services.AddEventHook&lt;InstanceFaultedCleanupEvent, InstanceFaultedCleanupEventHook&gt;();
/// </code>
/// </remarks>
public sealed class InstanceFaultedCleanupEventHook(
    ILogger<InstanceFaultedCleanupEventHook> logger,
    IInstanceCancellationService cancellationService,
    ICurrentSchema currentSchema,
    IUnitOfWorkManager unitOfWorkManager) : IEventPublishHook<InstanceFaultedCleanupEvent>
{
    /// <summary>
    /// Executes hook logic before the InstanceFaultedCleanupEvent is published.
    /// Cancels all active scheduled jobs for the faulted instance.
    /// </summary>
    /// <param name="eventData">The strongly-typed event data.</param>
    /// <param name="context">The hook context with metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hook execution result.</returns>
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceFaultedCleanupEvent eventData,
        EventHookContext context,
        CancellationToken cancellationToken = default)
    {
        logger.InstanceFaultedCleanupHookProcessing(eventData.InstanceId, eventData.Flow);

        try
        {
            await ProcessLocalAsync(eventData, cancellationToken);

            return EventHookResult.Ok(new Dictionary<string, string>
            {
                ["hook_executed"] = "true",
                ["instance_id"] = eventData.InstanceId.ToString(),
                ["event_type"] = "faulted_cleanup"
            });
        }
        catch (Exception ex)
        {
            logger.InstanceFaultedCleanupHookFailed(ex, eventData.InstanceId);

            return EventHookResult.Fail(ex, new Dictionary<string, string>
            {
                ["hook_error"] = "InstanceFaultedCleanupHookFailed"
            });
        }
    }

    /// <summary>
    /// Processes the instance fault cleanup locally with proper schema and UoW scope.
    /// </summary>
    /// <param name="eventData">The event data containing instance fault details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ProcessLocalAsync(InstanceFaultedCleanupEvent eventData, CancellationToken cancellationToken)
    {
        using (currentSchema.Use(eventData.Flow))
        {
            await using (var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
                         {
                             Scope = UnitOfWorkScopeOption.RequiresNew
                         }, cancellationToken))
            {
                await cancellationService.ProcessCancellationAsync(eventData.InstanceId, cancellationToken);
                await uow.SaveChangesAsync(cancellationToken);
                await uow.CommitAsync(cancellationToken);
            }
        }
    }
}
