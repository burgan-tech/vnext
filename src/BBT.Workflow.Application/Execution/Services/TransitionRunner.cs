using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Orchestrates committed transition stages and post-commit continuation handoff.
/// Each stage gets an isolated workflow scope and RequiresNew UoW. Post-commit jobs run only
/// after that scope has committed and disposed; an inline parent continuation starts as another
/// fully isolated stage rather than reusing the pre-handoff tracked aggregate.
/// </summary>
public sealed class TransitionRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<TransitionRunner> logger) : ITransitionRunner
{
    private const int MaxRunnerStages = 50;

    /// <inheritdoc />
    /// <summary>
    /// Runs one or more committed transition stages. A post-commit barrier may hand a fresh
    /// identity-only continuation back to the runner; every such continuation repeats the full
    /// workflow-scope/UoW/core lifecycle.
    /// </summary>
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var stageContext = context;

        // ChainDepth is zero-based, so the configured depth of 50 permits stages 0 through 50.
        // The next continuation (depth 51) is rejected after the final allowed stage commits.
        for (var stage = 0; stage <= MaxRunnerStages; stage++)
        {
            // ExecuteWithScopeAsync does not return until the stage UoW and workflow DI scope
            // have both disposed. TransitionPipeline's own lock already ended when core returned.
            var stageResult = await ExecuteWithScopeAsync(stageContext, cancellationToken);
            if (!stageResult.IsSuccess)
                return Result<TransitionOutput>.Fail(stageResult.Error);

            var coreOutput = stageResult.Value!;
            if (coreOutput.Continuations.PostCommitJobs.Count == 0)
                return Result<TransitionOutput>.Ok(coreOutput.Output);

            // Capture immutable identity/request data before handlers are allowed to touch the
            // stale execution context's response/directive fields.
            var parentSnapshot = PostCommitParentSnapshot.From(coreOutput.ExecutionContext);
            var coordinationResult = await CoordinatePostCommitAsync(
                parentSnapshot,
                coreOutput.ExecutionContext,
                cancellationToken);
            if (!coordinationResult.IsSuccess)
                return Result<TransitionOutput>.Fail(coordinationResult.Error);

            var decision = coordinationResult.Value!;
            if (decision.FaultRequest is not null)
            {
                return await MutateParentAsync(
                    parentSnapshot,
                    (service, ct) => service.FaultAsync(parentSnapshot, decision.FaultRequest, ct),
                    cancellationToken);
            }

            if (decision.NextContext is not null)
            {
                stageContext = decision.NextContext;
                continue;
            }

            // HandoffToChild (and a ContinueParent job with no remaining continuation) settles
            // from a fresh authoritative reload. The old outer NextTransition is never executed.
            return await MutateParentAsync(
                parentSnapshot,
                (service, ct) => service.SettleAsync(parentSnapshot, coreOutput.Continuations, ct),
                cancellationToken);
        }

        logger.LogWarning(
            "Transition runner stage depth exceeded {MaxStages} for transition {TransitionKey}",
            MaxRunnerStages,
            stageContext.TransitionKey);
        return Result<TransitionOutput>.Fail(
            WorkflowErrors.TransitionChainDepthExceeded(
                MaxRunnerStages + 1,
                MaxRunnerStages,
                stageContext.TransitionKey));
    }

    private Task<Result<PostCommitCoordinationResult>> CoordinatePostCommitAsync(
        PostCommitParentSnapshot snapshot,
        TransitionExecutionContext sourceContext,
        CancellationToken cancellationToken)
    {
        return scopeFactory.ExecuteWithWorkflowAsync(
            snapshot.Domain,
            snapshot.WorkflowKey,
            snapshot.WorkflowVersion,
            async (sp, ct) =>
            {
                var currentUser = sp.GetRequiredService<ICurrentUser>();
                var coordinator = sp.GetRequiredService<IPostCommitTransitionCoordinator>();
                using (currentUser.ChangeFromHeaders(snapshot.Headers))
                {
                    return await coordinator.CoordinateAsync(sourceContext, ct);
                }
            },
            cancellationToken);
    }

    private Task<Result<TransitionOutput>> MutateParentAsync(
        PostCommitParentSnapshot snapshot,
        Func<IPostCommitParentMutationService, CancellationToken, Task<Result<TransitionOutput>>> mutation,
        CancellationToken cancellationToken)
    {
        return scopeFactory.ExecuteWithWorkflowAsync(
            snapshot.Domain,
            snapshot.WorkflowKey,
            snapshot.WorkflowVersion,
            async (sp, ct) =>
            {
                var currentUser = sp.GetRequiredService<ICurrentUser>();
                var mutationService = sp.GetRequiredService<IPostCommitParentMutationService>();
                using (currentUser.ChangeFromHeaders(snapshot.Headers))
                {
                    return await mutation(mutationService, ct);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Executes the transition in a new DI scope with RequiresNew UoW.
    /// This ensures complete isolation from any ambient UoW.
    /// Before commit, stages deferred domain events collected during pipeline execution.
    /// Durable hooks run after commit from the UoW completion callback.
    /// Uses the ExecuteWithWorkflowAsync extension for scope + workflow loading.
    /// </summary>
    private Task<Result<TransitionCoreOutput>> ExecuteWithScopeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        return scopeFactory.ExecuteWithWorkflowAsync(context.Domain, context.WorkflowKey, context.WorkflowVersion,
            async (sp, ct) =>
            {
                var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                var core = sp.GetRequiredService<IWorkflowExecutionCore>();
                var currentUser = sp.GetRequiredService<ICurrentUser>();

                using (currentUser.ChangeFromHeaders(context.Headers))
                {
                    await using var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

                    var coreResult = await core.ExecuteTransitionCoreAsync(context, ct);
                    if (!coreResult.IsSuccess)
                        return Result<TransitionCoreOutput>.Fail(coreResult.Error);

                    await PublishDeferredEventsAsync(sp, uowManager, coreResult.Value!, ct);
                    
                    await uow.CommitAsync(ct);
                    
                    return coreResult;
                }
            }, cancellationToken);
    }

    /// <summary>
    /// Stages deferred domain events via IDistributedEventBus before UoW commit.
    /// Each event passes through HookedDistributedEventBus; durable hooks are registered for post-commit execution.
    /// Events include pre-extracted metadata from AddDistributedEvent time.
    /// </summary>
    private async Task PublishDeferredEventsAsync(
        IServiceProvider sp,
        IUnitOfWorkManager uowManager,
        TransitionCoreOutput coreOutput,
        CancellationToken ct)
    {
        if (coreOutput.DeferredEvents.Count == 0)
            return;

        var eventBus = sp.GetRequiredService<IDistributedEventBus>();

        foreach (var envelope in coreOutput.DeferredEvents)
        {
            await eventBus.PublishAsync(envelope.Event, envelope.Metadata, cancellationToken: ct);
        }
    }
}
