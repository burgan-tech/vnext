using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Hook executed before InstanceCompletedCleanupEvent is published.
/// Cancels all scheduled jobs for the completed instance.
/// </summary>
/// <remarks>
/// Register this hook in DI using:
/// <code>
/// services.AddEventHook&lt;InstanceCompletedCleanupEvent, InstanceCompletedCleanupEventHook&gt;();
/// </code>
/// </remarks>
public sealed class InstanceCompletedCleanupEventHook(
    ILogger<InstanceCompletedCleanupEventHook> logger,
    IInstanceCancellationService cancellationService,
    ICurrentSchema currentSchema,
    IUnitOfWorkManager unitOfWorkManager) : IEventPublishHook<InstanceCompletedCleanupEvent>
{
    /// <summary>
    /// Executes hook logic before the InstanceCompletedCleanupEvent is published.
    /// Cancels all active scheduled jobs for the completed instance.
    /// </summary>
    /// <param name="eventData">The strongly-typed event data.</param>
    /// <param name="context">The hook context with metadata.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The hook execution result.</returns>
    public async Task<EventHookResult> BeforePublishAsync(
        InstanceCompletedCleanupEvent eventData,
        EventHookContext context,
        CancellationToken cancellationToken = default)
    {
        logger.InstanceCompletedCleanupHookProcessing(eventData.InstanceId, eventData.Flow);

        try
        {
            await ProcessLocalAsync(eventData, cancellationToken);

            return EventHookResult.Ok(new Dictionary<string, string>
            {
                ["hook_executed"] = "true",
                ["instance_id"] = eventData.InstanceId.ToString(),
                ["event_type"] = "completed_cleanup"
            });
        }
        catch (Exception ex)
        {
            logger.InstanceCompletedCleanupHookFailed(ex, eventData.InstanceId);

            return EventHookResult.Fail(ex, new Dictionary<string, string>
            {
                ["hook_error"] = "InstanceCompletedCleanupHookFailed"
            });
        }
    }

    /// <summary>
    /// Processes the instance completion cleanup locally with proper schema and UoW scope.
    /// </summary>
    /// <param name="eventData">The event data containing instance completion details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ProcessLocalAsync(InstanceCompletedCleanupEvent eventData, CancellationToken cancellationToken)
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
