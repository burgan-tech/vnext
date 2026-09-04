using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Telemetry;
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

    private async Task<Result<PostCommitCoordinationResult>> CoordinatePostCommitAsync(
        PostCommitParentSnapshot snapshot,
        TransitionExecutionContext sourceContext,
        CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "PostCommit.Coordinate", sourceContext.TransitionKey);
        var result = await scopeFactory.ExecuteWithWorkflowAsync(
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
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
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
        // The context is handed in as the carrier: it may already hold the definition the intake
        // resolved, and when it does not, the scope's own load lands on it so the pipeline's
        // context factory reuses it instead of resolving the same flow a third time.
        return scopeFactory.ExecuteWithWorkflowAsync(context.Domain, context.WorkflowKey, context.WorkflowVersion,
            async (sp, ct) =>
            {
                var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                var core = sp.GetRequiredService<IWorkflowExecutionCore>();
                var currentUser = sp.GetRequiredService<ICurrentUser>();
                var terminalRelay = sp.GetRequiredService<ISubflowTerminalRelay>();

                using (currentUser.ChangeFromHeaders(context.Headers))
                {
                    await using var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

                    var coreResult = await core.ExecuteTransitionCoreAsync(context, ct);
                    if (!coreResult.IsSuccess)
                        return Result<TransitionCoreOutput>.Fail(coreResult.Error);

                    using (PipelineStepActivityHelper.StartTransitionActivity(
                               "Events.PublishDeferred", context.TransitionKey))
                    {
                        await PublishDeferredEventsAsync(sp, uowManager, coreResult.Value!, ct);
                    }

                    // The transaction commit — everything the hop wrote reaching the database at
                    // once. It sat outside every span, so a slow commit read as time the hop spent
                    // nowhere.
                    ActivityContext commitContext;
                    using (var commitActivity = PipelineStepActivityHelper.StartTransitionActivity(
                               "Uow.Commit", context.TransitionKey))
                    {
                        await uow.CommitAsync(ct);
                        commitContext = commitActivity?.Context ?? default;
                    }

                    // The activation episode closes HERE, not at Transition.Settle: the settlement's
                    // Busy→Active write only becomes visible to a client polling the state function
                    // once this commit lands. Emitted while the transaction (job span or server
                    // span) is still Activity.Current, parented to the lane anchor with its start
                    // backdated to the originating request — see ActivationActivity.
                    var executionContext = coreResult.Value!.ExecutionContext;
                    if (executionContext.Directives.Activation is { } verdict)
                    {
                        ActivationActivity.Emit(executionContext, verdict, commitContext);
                        if (verdict.CasFlipped)
                            Activity.Current?.AddEvent(new ActivityEvent("instance.available.committed"));
                    }

                    // The activation episode closes HERE, not at Transition.Settle: the settlement's
                    // Busy→Active write only becomes visible to a client polling the state function
                    // once this commit lands. Emitted while the transaction (job span or server
                    // span) is still Activity.Current, parented to the lane anchor with its start
                    // backdated to the originating request — see ActivationActivity.
                    var executionContext = coreResult.Value!.ExecutionContext;
                    if (executionContext.Directives.Activation is { } verdict)
                    {
                        ActivationActivity.Emit(executionContext, verdict);
                        if (verdict.CasFlipped)
                            Activity.Current?.AddEvent(new ActivityEvent("instance.available.committed"));
                    }

                    // Terminal relay: subflow terminal events settle the parent IMMEDIATELY as a command —
                    // awaited here so a sync chain's response follows the settled chain, and an async job
                    // relays with gap ≈ 0. The outbox rows written pre-commit stay the durable record; the
                    // Inbox handlers are the backup and ISubItemTerminalGuard absorbs the duplicate.
                    await terminalRelay.RelayAsync(coreResult.Value!.DeferredEvents, ct);

                    return coreResult;
                }
            }, cancellationToken, carrier: context);
    }

    /// <summary>
    /// Stages deferred domain events via IDistributedEventBus before UoW commit.
    /// Each event passes through TraceStampingDistributedEventBus, which stamps trace context and
    /// delegates — every event rides the outbox.
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
