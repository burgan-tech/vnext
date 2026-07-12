using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Orchestrates transition execution with isolated DI scope and UoW.
/// Transition chaining (auto/scheduled) is now handled by TransitionPipeline via sync dispatch.
/// This runner focuses on UoW lifecycle management for a single transition execution.
/// Uses ExecuteWithWorkflowAsync extension for automatic workflow loading and context management.
/// Deferred domain events are published according to
/// <see cref="WorkflowExecutionOptions.EventPublishingMode"/>.
/// </summary>
public sealed class TransitionRunner(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<TransitionRunner> logger) : ITransitionRunner
{
    /// <inheritdoc />
    /// <summary>
    /// Runs a transition in its own DI scope + RequiresNew UoW.
    /// Sync dispatch chain for auto transitions is managed by TransitionPipeline.
    /// </summary>
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var hopResult = await ExecuteWithScopeAsync(context, cancellationToken);
        if (!hopResult.IsSuccess)
            return Result<TransitionOutput>.Fail(hopResult.Error);

        var coreOutput = hopResult.Value!;
        return Result<TransitionOutput>.Ok(coreOutput.Output);
    }

    /// <summary>
    /// Executes the transition in a new DI scope with RequiresNew UoW.
    /// This ensures complete isolation from any ambient UoW.
    /// After commit, publishes deferred domain events collected during pipeline execution.
    /// Uses ExecuteWithWorkflowAsync extension for automatic workflow loading and IWorkflowContext setup.
    /// </summary>
    private Task<Result<TransitionCoreOutput>> ExecuteWithScopeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var transactionalOutbox =
            executionOptions.Value.EventPublishingMode == WorkflowEventPublishingMode.TransactionalOutbox;

        return scopeFactory.ExecuteWithWorkflowAsync(context.Domain, context.WorkflowKey, context.WorkflowVersion,
            async (sp, ct) =>
            {
                var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                var core = sp.GetRequiredService<IWorkflowExecutionCore>();
                var currentUser = sp.GetRequiredService<ICurrentUser>();

                using (currentUser.ChangeFromHeaders(context.Headers))
                {
                    TransitionCoreOutput coreOutput;

                    await using (var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew }))
                    {
                        var coreResult = await core.ExecuteTransitionCoreAsync(context, ct);
                        if (!coreResult.IsSuccess)
                            return Result<TransitionCoreOutput>.Fail(coreResult.Error);

                        coreOutput = coreResult.Value!;

                        // Legacy: publish inside the business UoW before its commit (historical order).
                        // TransactionalOutbox: defer publishing until AFTER the business commit so the
                        // events are written as a separate durable, transactional outbox envelope.
                        if (!transactionalOutbox)
                            await PublishDeferredEventsAsync(sp, coreOutput, ct);

                        await uow.CommitAsync(ct);
                    }

                    if (transactionalOutbox)
                        await PublishDeferredEventsTransactionalAsync(sp, uowManager, coreOutput, ct);

                    return Result<TransitionCoreOutput>.Ok(coreOutput);
                }
            }, cancellationToken);
    }

    /// <summary>
    /// Legacy publish: emits deferred domain events via IDistributedEventBus within the ambient
    /// (business) UoW. Each event passes through HookedDistributedEventBus, preserving hook behavior.
    /// Events include pre-extracted metadata from AddDistributedEvent time.
    /// </summary>
    private async Task PublishDeferredEventsAsync(
        IServiceProvider sp,
        TransitionCoreOutput coreOutput,
        CancellationToken ct)
    {
        if (coreOutput.DeferredEvents.Count == 0)
            return;

        var eventBus = sp.GetRequiredService<IDistributedEventBus>();

        foreach (var envelope in coreOutput.DeferredEvents)
        {
            try
            {
                await eventBus.PublishAsync(envelope.Event, envelope.Metadata, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to publish deferred event {EventType} for transition",
                    envelope.Event.GetType().Name);
            }
        }
    }

    /// <summary>
    /// TransactionalOutbox publish: after the business state is durably committed, writes the
    /// deferred domain events to the outbox in a dedicated <c>RequiresNew, IsTransactional=true</c>
    /// UoW that commits them atomically as one envelope. The outbox worker then delivers them
    /// at-least-once (broker → inbox handler), decoupled from the pipeline's per-step commits.
    /// A failure here does not undo the already-committed business state; it is logged and the
    /// idempotent retry/reaper path recovers the missed events.
    /// </summary>
    private async Task PublishDeferredEventsTransactionalAsync(
        IServiceProvider sp,
        IUnitOfWorkManager uowManager,
        TransitionCoreOutput coreOutput,
        CancellationToken ct)
    {
        if (coreOutput.DeferredEvents.Count == 0)
            return;

        var eventBus = sp.GetRequiredService<IDistributedEventBus>();

        try
        {
            await using var uow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            foreach (var envelope in coreOutput.DeferredEvents)
                await eventBus.PublishAsync(envelope.Event, envelope.Metadata, cancellationToken: ct);

            await uow.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to write {Count} deferred event(s) to the outbox for transition; " +
                "business state is committed and recovery relies on the idempotent retry path",
                coreOutput.DeferredEvents.Count);
        }
    }
}