using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Execution.PostCommit.Relay;
using BBT.Workflow.Instances;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
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
            {
                // E31. The post-commit work failed and the policy classified the error as the
                // client's, so nothing downstream touches the status — this is the one exit that
                // runs neither Settle nor Fault. What the parent needs depends on WHICH job failed,
                // and the two producers partition the case exactly:
                //
                //   ForwardToSubflowJob  — queued at order 10, which skips to Finalize, so the
                //                          parent changed no state. Release the Busy this run took.
                //   StartSubflowJob      — queued at order 70, downstream of ChangeState, so the
                //                          parent already moved with no child to show for it.
                //                          Fault, which is visible and retryable; releasing would
                //                          advertise a healthy instance that never started a child.
                //
                // Busy has no recovery API (retry requires Faulted), so doing nothing strands every
                // level until a human intervenes. The original error surfaces unchanged either way.
                await CompensateFailedCoordinationAsync(
                    parentSnapshot,
                    coreOutput,
                    coordinationResult.Error,
                    cancellationToken);
                return Result<TransitionOutput>.Fail(coordinationResult.Error);
            }

            var decision = coordinationResult.Value!;
            if (decision.FaultRequest is not null)
            {
                return await MutateParentAsync(
                    "PostCommit.Fault",
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
                "PostCommit.Settle",
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
        // The committed context already holds the definition this stage ran with; the fresh scope
        // reuses it instead of re-resolving the same coordinates from the component cache.
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
            cancellationToken,
            resolvedWorkflow: sourceContext.Workflow);
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
    }

    /// <summary>
    /// Compensates a failed post-commit coordination according to the job kind that failed.
    /// </summary>
    private async Task CompensateFailedCoordinationAsync(
        PostCommitParentSnapshot snapshot,
        TransitionCoreOutput coreOutput,
        Error error,
        CancellationToken cancellationToken)
    {
        // ContinuationSet is a non-consuming projection, so the jobs are still readable here even
        // though the coordinator consumed the directives.
        // Testing for ANY StartSubflowJob (rather than the one that actually failed) is safe only
        // because the two post-commit job kinds are disjoint within one hop: ForwardToSubflowJob is
        // queued at order 10 (ForwardToActiveSubflowStep), which sets SkipToOrder = Finalize, so the
        // order-70 StartSubflowJob (HandleSubFlowStep) can never also be queued in the same run.
        var startedSubflow = coreOutput.Continuations.PostCommitJobs.OfType<StartSubflowJob>().Any();

        if (startedSubflow)
        {
            // A lost CAS on the start hop yields no verdict: the level that actually flipped Busy
            // is the one responsible for compensating it, not this one.
            // Defensive: not known to be reachable today. OwnsStatus is false only for updateData
            // (HandleSubFlowStep short-circuits before order 70 for it) and for the
            // IsSubflowForward branch (ForwardToActiveSubflowStep skips to Finalize before order 70,
            // or — for a parent shared transition — SharedTransitionTargetSelfWhenInSubFlowSpecification
            // forces target=$self and HandleSubFlowStep's same-state idempotent check takes the
            // no-new-job path), so a StartSubflowJob and OwnsStatus == false cannot coincide today;
            // pinned by RunAsync_WhenStartSubflowCoordinationFails_AndContextDoesNotOwnStatus_CompensatesNothing.
            if (!coreOutput.ExecutionContext.OwnsStatus)
                return;

            logger.SubflowStartCoordinationFaulted(snapshot.InstanceId, snapshot.TransitionKey, error.Code);
            var faultResult = await MutateParentAsync(
                "PostCommit.Fault",
                snapshot,
                (service, ct) => service.FaultAsync(
                    snapshot,
                    new PostCommitFaultRequest(error.Code, error.Message ?? "Post-commit coordination failed"),
                    ct),
                cancellationToken);
            if (!faultResult.IsSuccess)
                logger.PostCommitJobFailed(snapshot.InstanceId, "PostCommit.Fault", faultResult.Error.Message ?? faultResult.Error.Code);
            return;
        }

        // ForwardToSubflowJob: order 10 skips to Finalize on failure, so the parent changed no
        // state. Undo exactly what the accept flipped, i.e. only when the accept marked the whole
        // chain Busy down to the leaf — a sync-origin forward never sets that flag (see
        // TransitionPipeline's IsSubflowForward branch), and there is no other Busy of this run's
        // own to release on this path.
        await ReleaseChainReserveAsync(
            snapshot, coreOutput.ExecutionContext.SubflowChainReserved, error, cancellationToken);
    }

    /// <summary>
    /// Compensates an accept-time subflow chain reserve after post-commit work failed without a
    /// fault request.
    /// <para>
    /// Deliberately NOT run on the fault path: <c>Instance.Fault</c> already cascades downward,
    /// raising <c>ChildSubflowFaultRequestedEvent</c> for every active SubFlow correlation, so the
    /// children fault instead of stranding. Releasing there would race that cascade and could put
    /// a level back to Active while its fault is still in flight.
    /// </para>
    /// <para>
    /// Identity only crosses the post-commit barrier: the stage scope that built the execution
    /// context is already disposed, so the release takes the instance id and lock key from the
    /// snapshot rather than the stale context. The release acquires the status lock itself and
    /// swallows its own failures — compensation must never mask the error being returned.
    /// </para>
    /// </summary>
    private async Task ReleaseChainReserveAsync(
        PostCommitParentSnapshot snapshot,
        bool subflowChainReserved,
        Error error,
        CancellationToken cancellationToken)
    {
        if (!subflowChainReserved)
            return;

        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            "PostCommit.ReleaseChainReserve", snapshot.TransitionKey);
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, snapshot.InstanceId.ToString());

        logger.SubflowChainReserveReleasing(snapshot.InstanceId, snapshot.TransitionKey, error.Code);

        await scopeFactory.ExecuteWithWorkflowAsync(
            snapshot.Domain,
            snapshot.WorkflowKey,
            snapshot.WorkflowVersion,
            async (sp, ct) =>
            {
                var admission = sp.GetRequiredService<ITransitionAdmissionService>();
                await admission.ReleaseSubflowChainAsync(snapshot.InstanceId, snapshot.LockKey, ct);
                return Result<bool>.Ok(true);
            },
            cancellationToken,
            resolvedWorkflow: snapshot.Workflow);
    }

    /// <summary>
    /// Runs one fresh-parent mutation (settle or fault) in its own workflow scope, under a single
    /// <c>PostCommit.Settle</c> / <c>PostCommit.Fault</c> span. Everything the mutation does — the
    /// status lock, the authoritative reload, <c>Transition.Settle</c>, its commit and the release —
    /// used to land directly on the transaction as unrelated siblings after
    /// <c>PostCommit.Coordinate</c>; the span names the phase they belong to.
    /// </summary>
    private async Task<Result<TransitionOutput>> MutateParentAsync(
        string operationName,
        PostCommitParentSnapshot snapshot,
        Func<IPostCommitParentMutationService, CancellationToken, Task<Result<TransitionOutput>>> mutation,
        CancellationToken cancellationToken)
    {
        using var activity = PipelineStepActivityHelper.StartTransitionActivity(
            operationName, snapshot.TransitionKey);
        activity?.SetTag(TelemetryConstants.TagNames.InstanceId, snapshot.InstanceId.ToString());

        // The snapshot carries the definition across the handoff (see PostCommitParentSnapshot.Workflow),
        // and the mutation service builds its fresh context from it — the scope has nothing to resolve.
        var result = await scopeFactory.ExecuteWithWorkflowAsync(
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
            cancellationToken,
            resolvedWorkflow: snapshot.Workflow);
        if (!result.IsSuccess)
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);

        return result;
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
                var relayDispatcher = sp.GetRequiredService<IPostCommitRelayDispatcher>();

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

                    // Post-commit relay: an event whose type has a registered relay is ALSO delivered
                    // to its receiver IMMEDIATELY as a command — awaited here so a sync chain's
                    // response follows the settled chain, and an async job relays with gap ≈ 0. The
                    // outbox rows written pre-commit stay the durable record; the Inbox handlers are
                    // the backup, absorbed by ISubItemTerminalGuard for the terminal events and by the
                    // per-sub-item lock plus monotonic stamp for the state channel. Events with no
                    // registered relay pass through untouched and travel the outbox alone.
                    await relayDispatcher.RelayAsync(coreResult.Value!.DeferredEvents, ct);

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
