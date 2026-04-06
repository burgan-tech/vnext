using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Hook executed before InstanceCompletedCleanupEvent is published.
/// Cancels all scheduled jobs for the completed instance.
/// Uses IServiceScopeFactory to create an isolated DI scope, avoiding EF transaction
/// conflicts when the hook runs inside an outer UoW's CommitAsync pipeline.
/// </summary>
/// <remarks>
/// Register this hook in DI using:
/// <code>
/// services.AddEventHook&lt;InstanceCompletedCleanupEvent, InstanceCompletedCleanupEventHook&gt;();
/// </code>
/// </remarks>
public sealed class InstanceCompletedCleanupEventHook(
    ILogger<InstanceCompletedCleanupEventHook> logger,
    IServiceScopeFactory scopeFactory) : IEventPublishHook<InstanceCompletedCleanupEvent>
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
    /// Processes the instance completion cleanup in an isolated DI scope with its own DbContext
    /// and UoW, preventing EF transaction conflicts with the caller's active transaction.
    /// </summary>
    /// <param name="eventData">The event data containing instance completion details.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task ProcessLocalAsync(InstanceCompletedCleanupEvent eventData, CancellationToken cancellationToken)
    {
        await scopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
        {
            var currentSchema = sp.GetRequiredService<ICurrentSchema>();
            var unitOfWorkManager = sp.GetRequiredService<IUnitOfWorkManager>();
            var cancellationService = sp.GetRequiredService<IInstanceCancellationService>();

            using (currentSchema.Use(eventData.Flow))
            {
                await using var uow = await unitOfWorkManager.BeginAsync(new UnitOfWorkOptions
                {
                    Scope = UnitOfWorkScopeOption.RequiresNew
                }, ct);

                await cancellationService.ProcessCancellationAsync(eventData.InstanceId, ct);
                await uow.SaveChangesAsync(ct);
                await uow.CommitAsync(ct);
                return Result.Ok();
            }
        }, cancellationToken);
    }
}
