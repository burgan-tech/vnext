using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Shared;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowCompletionService" />
public sealed class SubflowCompletionService(
    IUnitOfWorkManager uowManager,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceCorrelationRepository correlationRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    IWorkflowExecutionService workflowExecutionService,
    ISubflowOutputMappingService outputMappingService,
    ITransitionLockScopeFactory transitionLockScopeFactory,
    ISubItemTerminalGuard terminalGuard,
    IDistributedEventBus eventBus,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<SubflowCompletionService> logger)
    : ISubflowCompletionService
{
    /// <summary>
    /// Attempt budget for re-publishing the terminal event from inside a correlation revert. Caps
    /// a pathological resume-fails-forever loop from re-arming the durable backup indefinitely.
    /// </summary>
    private const int MaxRearmAttempts = 5;

    private LockAcquireWait TerminalLockWait => executionOptions.Value.SubItemTerminalLockRetry.ToLockAcquireWait();

    /// <inheritdoc />
    public async Task CompletionAsync(
        FlowCompletedInput completedInput,
        CancellationToken cancellationToken = default)
    {
        // Start activity at the very beginning to capture the full operation trace
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Completion/{completedInput.Domain}/{completedInput.Flow}");
        SubFlowActivityHelper.EnrichWithCompletion(
            activity,
            completedInput.SubInstanceId,
            completedInput.InstanceId,
            completedInput.Domain,
            completedInput.Flow);
        activity?.SetTag(TelemetryConstants.TagNames.FlowVersion, completedInput.Version ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.RootInstanceId, completedInput.RootInstanceId?.ToString() ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, completedInput.InstanceId.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.SubItemOutcome, SubItemTerminalOutcome.Completed.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.TerminationOrigin, "legacy");
        activity?.SetTag(TelemetryConstants.TagNames.TerminationInitiator, "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.TerminationCascadeId, "N/A");

        var scopeProperties = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = completedInput.Domain,
            [TelemetryConstants.TagNames.Flow] = completedInput.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = completedInput.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = completedInput.InstanceId,
            [TelemetryConstants.TagNames.RootInstanceId] = completedInput.RootInstanceId?.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.ParentInstanceId] = completedInput.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = completedInput.SubInstanceId,
            [TelemetryConstants.TagNames.SubItemType] = "N/A",
            [TelemetryConstants.TagNames.SubItemOutcome] = SubItemTerminalOutcome.Completed.ToString(),
            [TelemetryConstants.TagNames.TerminationOrigin] = "legacy",
            [TelemetryConstants.TagNames.TerminationInitiator] = "N/A",
            [TelemetryConstants.TagNames.TerminationCascadeId] = "N/A"
        };
        using (logger.BeginScope(scopeProperties))
        {
            // Check if this domain is handled by this runtime instance
            try
            {
                runtimeInfoProvider.Check(completedInput.Domain);
            }
            catch (NotFoundDomainException)
            {
                // Silently ignore - this is expected in multi-domain scenarios
                activity?.SetTag("vnext.subflow.result", "domain_not_handled");
                return;
            }

            try
            {
                Instance? parentInstance;
                Definitions.Workflow? parentWorkflow = null;
                Result<SubflowOutputMappingPlan>? preparedMapping = null;

                // Per-subInstance terminal lock, independent of the main-flow lock and reserved keys:
                // a long-held chain lease never blocks the signal, parallel SubProcess terminal-closes
                // don't contend, and only duplicate deliveries of the SAME subInstance serialize.
                // Sync/async safe: distinct from every instance-status lock key (no self-deadlock).
                var lockKey = $"vnext:{completedInput.Domain}:{completedInput.Flow}:{completedInput.InstanceId}:sub:{completedInput.SubInstanceId:N}";

                // Lock-free duplicate short-circuit. This signal is delivered at least twice by
                // design (DurablePostCommit hook + Inbox worker), so the common case is a duplicate
                // whose work is already persisted. Answering it from a read-only snapshot keeps it
                // off the distributed lock entirely; only genuinely-open deliveries contend.
                var probeResult = await terminalGuard.ProbeWithSnapshotAsync(
                    completedInput.InstanceId,
                    completedInput.SubInstanceId,
                    SubItemTerminalOutcome.Completed,
                    cancellationToken);
                var probe = probeResult.Decision;

                if (probe != SubItemTerminalProbe.Proceed)
                {
                    activity?.SetTag("vnext.subflow.result",
                        probe == SubItemTerminalProbe.AlreadySettled
                            ? "correlation_already_settled_prelock"
                            : "terminal_outcome_conflict_prelock");
                    SubFlowActivityHelper.SetSuccess(activity);
                    return;
                }

                // Workflow resolution and script compilation are immutable preparation work. Keep
                // them outside the terminal lock; only the authoritative read, execution and
                // persistence remain in the critical section. The null-snapshot fallback below is
                // retained for the narrow race where the correlation was not visible to the probe.
                if (probeResult.SubFlowType?.Equals(SubFlowType.SubFlow) == true
                    && probeResult.ParentState is not null)
                {
                    var workflowResult = await componentCacheStore.GetFlowAsync(
                        completedInput.Domain,
                        completedInput.Flow,
                        completedInput.Version,
                        cancellationToken);
                    if (workflowResult.IsSuccess)
                    {
                        parentWorkflow = workflowResult.Value!;
                        preparedMapping = await outputMappingService.PrepareAsync(
                            completedInput.InstanceId,
                            parentWorkflow,
                            probeResult.ParentState,
                            cancellationToken);
                    }
                }

                // Bounded wait rather than fail-fast: a duplicate that arrives while the original is
                // still inside its transaction cannot see the pending write, so short-circuiting
                // above is impossible and failing immediately would push it into a full broker
                // re-delivery cycle. The critical section is one short transaction — waiting it out
                // is far cheaper, and the re-read after acquisition settles the outcome correctly.
                await using (var lockScope = await transitionLockScopeFactory.AcquireAsync(
                                 lockKey, TerminalLockWait, cancellationToken))
                {
                    if (!lockScope.IsAcquired)
                    {
                        logger.SubItemTerminalLockNotAcquired(lockKey, SubItemTerminalOutcome.Completed.ToString());
                        throw new SubflowTerminalLockNotAcquiredException(
                            completedInput.Domain,
                            completedInput.Flow,
                            completedInput.InstanceId.ToString(),
                            SubItemTerminalOutcome.Completed.ToString());
                    }

                    await using var correlationUow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
                    // Data must be loaded here: output mapping appends a new data version via
                    // Instance.AddData, whose version/IsLatest math reads the in-memory data list.
                    parentInstance = await instanceRepository.FindForSubflowCompletionAsync(
                        completedInput.InstanceId,
                        completedInput.SubInstanceId,
                        cancellationToken);

                    if (parentInstance == null)
                    {
                        logger.InstanceNotFound(completedInput.InstanceId, completedInput.Flow);
                        activity?.SetTag("vnext.subflow.result", "parent_not_found");
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    var correlation = parentInstance.FindCorrelationBySubInstanceId(completedInput.SubInstanceId);
                    if (correlation == null)
                    {
                        logger.SubFlowCorrelationNotFound(completedInput.SubInstanceId);
                        activity?.SetTag("vnext.subflow.result", "correlation_not_found");
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    activity?.SetTag(TelemetryConstants.TagNames.SubItemType, correlation.SubFlowType.Code);
                    scopeProperties[TelemetryConstants.TagNames.SubItemType] = correlation.SubFlowType.Code;

                    if (correlation.IsCompleted)
                    {
                        if (correlation.TerminalOutcome == SubItemTerminalOutcome.Completed)
                        {
                            logger.SubItemTerminalDuplicate(
                                SubItemTerminalOutcome.Completed.ToString(),
                                completedInput.InstanceId,
                                completedInput.SubInstanceId);
                        }
                        else
                        {
                            logger.SubItemTerminalConflict(
                                completedInput.InstanceId,
                                completedInput.SubInstanceId,
                                correlation.TerminalOutcome?.ToString() ?? "legacy",
                                SubItemTerminalOutcome.Completed.ToString());
                        }

                        activity?.SetTag("vnext.subflow.result", "correlation_already_terminal");
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    // A terminal parent still needs an active SubProcess correlation closed, but
                    // a blocking SubFlow must not mutate or resume an already-terminal parent.
                    if (parentInstance.IsCompleted &&
                        correlation.SubFlowType.Equals(SubFlowType.SubFlow))
                    {
                        activity?.SetTag("vnext.subflow.result", "parent_already_terminal");
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    // Complete correlation and persist changes
                    await CompleteAndPersistCorrelationAsync(
                        parentInstance,
                        completedInput.SubInstanceId,
                        completedInput.InstanceId,
                        completedInput.CompletedState,
                        completedInput.CompletedAt,
                        cancellationToken);

                    // If this is a SubProcess (non-blocking), commit and return
                    if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
                    {
                        activity?.SetTag("vnext.subflow.type", "subprocess");
                        SubFlowActivityHelper.SetSuccess(activity);
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    activity?.SetTag("vnext.subflow.type", "subflow");

                    // Load parent workflow definition for output mapping
                    var parentWorkflowResult = parentWorkflow is not null
                        ? Result<Definitions.Workflow>.Ok(parentWorkflow)
                        : await componentCacheStore.GetFlowAsync(
                            completedInput.Domain,
                            completedInput.Flow,
                            completedInput.Version,
                            cancellationToken);

                    if (!parentWorkflowResult.IsSuccess)
                    {
                        logger.LogWarning("Failed to get parent workflow {Flow} for SubFlow completion: {ErrorCode}",
                            completedInput.Flow, parentWorkflowResult.Error.Code);

                        // Parent definition unavailable — resuming can never succeed, and a silent return
                        // would strand the parent (and every ancestor) forever. Fault the parent so the
                        // error propagates upward via InstanceSubFaultedEvent instead of leaving it stuck.
                        var loadIncident = InstanceIncidentFactory.Create(
                            state: parentInstance.GetCurrentState,
                            transition: string.Empty,
                            taskKey: null,
                            message: $"Parent workflow '{completedInput.Flow}' could not be loaded for SubFlow completion",
                            errorCode: parentWorkflowResult.Error.Code ?? WorkflowErrorCodes.NotFoundWorkflow,
                            errorLayer: "SubFlow",
                            stackTrace: parentWorkflowResult.Error.Detail);
                        parentInstance.AddIncident(loadIncident);
                        parentInstance.Fault(completedInput.Domain, completedInput.Sync);
                        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    parentWorkflow = parentWorkflowResult.Value!;

                    var mappingPreparation = preparedMapping.HasValue
                        ? preparedMapping.Value
                        : await outputMappingService.PrepareAsync(
                            parentInstance.Id,
                            parentWorkflow,
                            correlation.ParentState,
                            cancellationToken);

                    var mappingResult = mappingPreparation.IsSuccess
                        ? await outputMappingService.ApplyPreparedAsync(
                        parentInstance,
                        parentWorkflow,
                        mappingPreparation.Value!,
                        completedInput.InstanceData,
                        cancellationToken)
                        : Result.Fail(mappingPreparation.Error);

                    if (!mappingResult.IsSuccess)
                    {
                        // Output-mapping failures are permanent at this boundary. Recoverable
                        // assembly-load contention is handled locally by CSharpEvaluator; an
                        // unclassified loader failure must not consume the Inbox retry budget and
                        // dead-letter while leaving the parent open. Faulting here makes the failure
                        // visible and propagates it to the grandparent via InstanceSubFaultedEvent.
                        var incident = InstanceIncidentFactory.Create(
                            state: parentInstance.GetCurrentState,
                            transition: string.Empty,
                            taskKey: null,
                            message: mappingResult.Error.Message ?? "SubFlow output mapping failed",
                            errorCode: mappingResult.Error.Code ?? WorkflowErrorCodes.SubflowOutputMappingFailed,
                            errorLayer: "SubFlow",
                            stackTrace: mappingResult.Error.Detail);
                        parentInstance.AddIncident(incident);
                        parentInstance.Fault(completedInput.Domain, completedInput.Sync);
                        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    await correlationUow.CommitAsync(cancellationToken);
                }

                logger.SubFlowPipelineResumed(parentInstance.Id);
                await ResumePipelineAsync(
                    parentInstance,
                    parentWorkflow!,
                    completedInput,
                    lockKey,
                    cancellationToken);

                // Resume has committed in its own UoW. Marking settlement is deliberately a tiny
                // set-based write in a new UoW; a marker-write failure must not compensate an
                // already successful resume, it only forfeits the duplicate fast path.
                await MarkSettlementBestEffortAsync(
                    completedInput.SubInstanceId,
                    completedInput.CompletedAt,
                    cancellationToken);

                SubFlowActivityHelper.SetSuccess(activity);
            }
            catch (Exception ex)
            {
                SubFlowActivityHelper.SetError(activity, ex.Message, ex);
                logger.SubFlowCompletionFailed(
                    ex,
                    completedInput.SubInstanceId,
                    completedInput.InstanceId);

                throw;
            }
        }
    }

    private async Task MarkSettlementBestEffortAsync(
        Guid subInstanceId,
        DateTime settledAt,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var markerUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
            await correlationRepository.TryMarkSettledAsync(
                subInstanceId,
                SubItemTerminalOutcome.Completed,
                settledAt,
                cancellationToken);
            await markerUow.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.SubItemSettlementMarkerWriteFailed(ex, nameof(SubItemTerminalOutcome.Completed), subInstanceId);
        }
    }

    /// <summary>
    /// Resumes automatic transitions and scheduled processes for the parent workflow after SubFlow completion.
    /// Runs after Phase 1 (correlation + mapping) is committed. TransitionRunner creates its own scope+UoW.
    /// On failure, reverts the correlation in a new UoW so the operation can be retried.
    /// </summary>
    private async Task ResumePipelineAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        FlowCompletedInput completedInput,
        string parentLockKey,
        CancellationToken cancellationToken)
    {
        var subInstanceId = completedInput.SubInstanceId;
        try
        {
            // The resume is a PARENT-instance operation: it runs after the completion commits, in its
            // own scope and UoW. So it belongs in the parent's lane, at the same level as the
            // parent's other transition hops — not nested inside the subflow's span, which is where
            // the old nesting accumulated. Falls back to the current lane when there is no parent
            // lane (a resume outside a subflow handoff), which is the pre-lane behaviour.
            using var resumeActivity = SubFlowActivityHelper.StartFlatLaneActivity(
                $"SubFlow.Resume/{parentWorkflow.Domain}/{parentWorkflow.Key}",
                WorkflowTraceLane.ParentLane ?? WorkflowTraceLane.Current);
            resumeActivity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
            resumeActivity?.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstance.Id.ToString());
            resumeActivity?.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subInstanceId.ToString());

            var input = new WorkflowExecutionContext
            {
                Domain = parentWorkflow.Domain,
                WorkflowKey = parentWorkflow.Key,
                WorkflowVersion = parentWorkflow.Version,
                InstanceId = parentInstance.Id.ToString(),
                TransitionKey = "", // For logging purposes only
                TriggerType = TriggerType.Manual,
                Mode = ExecMode.Resume, // Use Resume mode for SubFlow completion
                // Preserve the completing chain's caller mode so the resumed parent chain
                // keeps starting/forwarding subflows synchronously when the caller was sync=true.
                CallerMode = completedInput.Sync ? ExecMode.Sync : ExecMode.Async,
                Headers = new Dictionary<string, string?>(),
                Actor = ExecutionActor.System,
                RequestedAt = DateTimeOffset.UtcNow,
                Execution = new ExecutionInfo
                {
                    ExecutionChainId = Guid.NewGuid().ToString("N"),
                    ChainDepth = 0,
                    ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep,
                    IsSubFlowResume = true,
                    SubFlowResumeInstanceId = subInstanceId
                }
            };

            var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);

            if (!result.IsSuccess)
            {
                // AutoTransitionConditionNotMet: normal — no matching auto-transition, instance stays Active.
                // InstanceCompleted: race condition — parent was completed between Phase 1 commit and
                // Phase 2 execution (e.g. timeout, cancel, or duplicate delivery). Both are non-fatal.
                var isSoftError = result.Error.Code == WorkflowErrorCodes.AutoTransitionConditionNotMet
                               || result.Error.Code == WorkflowErrorCodes.InstanceCompleted;

                if (!isSoftError)
                {
                    logger.TransitionRuleFailed(
                        "subflow",
                        parentInstance.Id,
                        result.Error.Message ?? "Unknown error");
                }

                if (!isSoftError)
                {
                    throw new SubflowCompletionException(
                        parentWorkflow.Domain,
                        parentWorkflow.Key,
                        parentInstance.Id.ToString(),
                        result.Error.Code,
                        result.Error.Message ?? "Unknown error");
                }
            }
        }
        catch (Exception ex)
        {
            logger.SubFlowCompletionFailed(
                ex,
                subInstanceId,
                parentInstance.Id);

            // Phase 1 is already committed; reacquire the parent lock and compensate from
            // authoritative persisted state so a concurrent terminal winner cannot be reopened.
            await RevertCorrelationInNewUowAsync(
                parentLockKey,
                completedInput,
                parentInstance.Id);
            throw;
        }
    }

    /// <summary>
    /// Completes the SubFlow correlation and persists the changes to the repository.
    /// For a blocking SubFlow, also resets the parent's EffectiveState back to its own CurrentState.
    /// </summary>
    private async Task CompleteAndPersistCorrelationAsync(
        Instance parentInstance,
        Guid subInstanceId,
        Guid parentInstanceId,
        string completedState,
        DateTime completedAt,
        CancellationToken cancellationToken)
    {
        var existingCorrelation = parentInstance.FindCorrelationBySubInstanceId(subInstanceId);
        existingCorrelation?.UpdateSubFlowState(completedState, completedAt);
        var correlation = parentInstance.CompleteCorrelation(
            subInstanceId,
            SubItemTerminalOutcome.Completed,
            completedAt);
        if (correlation == null)
            return;

        if (correlation.SubFlowType.Equals(SubFlowType.SubFlow))
        {
            // Reset parent's EffectiveState back to its own CurrentState for blocking SubFlows.
            // A SubProcess completion only closes its persisted correlation.
            parentInstance.SetEffectiveState(parentInstance.GetCurrentState);
        }
        else
        {
            // SubProcess has no resume phase; its terminal write is already fully settled.
            correlation.MarkSettled(completedAt);
        }

        logger.SubFlowCorrelationCompleted(subInstanceId, parentInstanceId);

        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
    }

    /// <summary>
    /// Reverts the SubFlow correlation in its own RequiresNew UoW and commits.
    /// Used when Phase 1 (correlation completion) succeeded but Phase 2 (pipeline resume) failed,
    /// so the correlation can be retried.
    /// </summary>
    private async Task RevertCorrelationInNewUowAsync(
        string parentLockKey,
        FlowCompletedInput originalInput,
        Guid parentInstanceId)
    {
        var subInstanceId = originalInput.SubInstanceId;
        try
        {
            var cancellationToken = CancellationToken.None;
            await using var lockScope = await transitionLockScopeFactory.AcquireAsync(parentLockKey, cancellationToken);
            if (!lockScope.IsAcquired)
            {
                throw new InvalidOperationException(
                    $"Parent instance compensation lock '{parentLockKey}' could not be acquired.");
            }

            await using var revertUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });

            await RevertAndPersistCorrelationAsync(subInstanceId, parentInstanceId, cancellationToken);

            // The duplicate-ACK window: a backup delivery that arrived after the phase-1 commit was
            // acknowledged from the IsCompleted flag and is now consumed, while this revert reopens
            // the correlation. Re-publish the terminal event in THIS UoW so a fresh durable delivery
            // commits atomically with the revert — the reopened work is never left without a carrier.
            var rearmAttempt = originalInput.RearmAttempt ?? 0;
            if (rearmAttempt >= MaxRearmAttempts)
            {
                logger.SubflowTerminalRearmExhausted(parentInstanceId, subInstanceId, rearmAttempt);
            }
            else
            {
                var rearmEvent = BuildTerminalEventFromInput(originalInput, rearmAttempt + 1);
                await eventBus.PublishAsync(rearmEvent, cancellationToken: cancellationToken);
                logger.SubflowTerminalRearmed(parentInstanceId, subInstanceId, rearmAttempt + 1);
            }

            await revertUow.CommitAsync(cancellationToken);
        }
        catch (Exception revertEx)
        {
            // Log but don't throw — the original exception from ResumePipelineAsync takes priority
            logger.SubItemCorrelationRevertFailed(revertEx, parentInstanceId, subInstanceId);
        }
    }

    /// <summary>
    /// Reconstructs the terminal event from the service's own input DTO — the exact inverse of
    /// <c>SubflowTerminalRelay.MapToFlowCompletedInput</c> (verbatim field copy, including the
    /// trace-lane anchors) — so a re-arm publishes the same fact the original delivery carried.
    /// </summary>
    private static InstanceSubCompletedEvent BuildTerminalEventFromInput(
        FlowCompletedInput input,
        int rearmAttempt) => new()
    {
        InstanceId = input.InstanceId,
        RootInstanceId = input.RootInstanceId,
        Domain = input.Domain,
        Flow = input.Flow,
        Version = input.Version,
        SubInstanceId = input.SubInstanceId,
        CompletedState = input.CompletedState,
        InstanceData = input.InstanceData,
        CompletedAt = input.CompletedAt,
        Duration = input.Duration,
        Sync = input.Sync,
        TraceRoot = input.TraceRoot,
        ParentTraceRoot = input.ParentTraceRoot,
        RearmAttempt = rearmAttempt
    };

    /// <summary>
    /// Reverts the SubFlow correlation and persists the changes to the repository.
    /// </summary>
    private async Task RevertAndPersistCorrelationAsync(
        Guid subInstanceId,
        Guid parentInstanceId,
        CancellationToken cancellationToken)
    {
        // S9 isolation rule: do NOT mutate the detached Phase-1 entity inside this new UoW —
        // reload by id so we operate on an entity tracked by the current scope's DbContext.
        // MUST load with ALL correlations: the default detail load filters completed
        // correlations out, which silently skipped the revert and left the parent stuck Busy.
        var tracked = await instanceRepository.FindWithAllCorrelationsAsync(parentInstanceId, cancellationToken)
                      ?? throw new InvalidOperationException(
                          $"Parent instance {parentInstanceId} was not found while reverting completion.");

        if (tracked.IsCompleted)
        {
            return;
        }

        var correlation = tracked.RevertCorrelation(subInstanceId);
        if (correlation == null)
        {
            logger.SubFlowCorrelationRevertTargetMissing(subInstanceId, parentInstanceId);
            return;
        }

        logger.SubFlowCorrelationReverted(subInstanceId, parentInstanceId);

        await instanceRepository.UpdateAsync(tracked, true, cancellationToken);
    }
}
