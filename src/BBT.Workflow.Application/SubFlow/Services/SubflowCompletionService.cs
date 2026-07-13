using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Shared;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowCompletionService" />
public sealed class SubflowCompletionService(
    IUnitOfWorkManager uowManager,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IRuntimeInfoProvider runtimeInfoProvider,
    IWorkflowExecutionService workflowExecutionService,
    ISubflowOutputMappingService outputMappingService,
    ILogger<SubflowCompletionService> logger)
    : ISubflowCompletionService
{
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

        using (logger.BeginScope(new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = completedInput.Domain,
            [TelemetryConstants.TagNames.Flow] = completedInput.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = completedInput.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = completedInput.InstanceId,
            [TelemetryConstants.TagNames.ParentInstanceId] = completedInput.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = completedInput.SubInstanceId
        }))
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
                Definitions.Workflow? parentWorkflow;
                
                // Transactional: the correlation-completion phase reads/mutates the parent instance,
                // reads the workflow definition from cache, and runs the (local) output-mapping script
                // — all bounded, with no remote/pipeline span (ResumePipelineAsync runs OUTSIDE this
                // unit). Required under SchemaSwitchingMode.TransactionLocal and lets correlation
                // events dispatch on commit.
                await using (var correlationUow =  uowManager.Begin(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true }))
                {
                    parentInstance = await instanceRepository.FindAsync(
                        completedInput.InstanceId, true, cancellationToken);

                    if (parentInstance == null)
                    {
                        logger.InstanceNotFound(completedInput.InstanceId, completedInput.Flow);
                        activity?.SetTag("vnext.subflow.result", "parent_not_found");
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    // Idempotency: if parent is already in a terminal state the subflow completion
                    // has already been processed (or the parent was terminated via another path).
                    // No pipeline resume is needed — just return cleanly.
                    if (parentInstance.Status.Equals(InstanceStatus.Completed) ||
                        parentInstance.Status.Equals(InstanceStatus.Faulted))
                    {
                        activity?.SetTag("vnext.subflow.result", "parent_already_terminal");
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

                    // Complete correlation and persist changes
                    await CompleteAndPersistCorrelationAsync(
                        parentInstance,
                        completedInput.SubInstanceId,
                        completedInput.InstanceId,
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
                    var parentWorkflowResult = await componentCacheStore.GetFlowAsync(
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

                    var mappingResult = await outputMappingService.ApplyAsync(
                        parentInstance,
                        parentWorkflow,
                        correlation.ParentState,
                        completedInput.InstanceData,
                        cancellationToken);

                    if (!mappingResult.IsSuccess)
                    {
                        // Output mapping failed — fault the parent instead of resuming the pipeline.
                        // Retrying would never succeed; faulting propagates the error to A via InstanceSubFaultedEvent.
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
                    completedInput.SubInstanceId,
                    completedInput.Sync,
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

    /// <summary>
    /// Resumes automatic transitions and scheduled processes for the parent workflow after SubFlow completion.
    /// Runs after Phase 1 (correlation + mapping) is committed. TransitionRunner creates its own scope+UoW.
    /// On failure, reverts the correlation in a new UoW so the operation can be retried.
    /// </summary>
    private async Task ResumePipelineAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        Guid subInstanceId,
        bool sync,
        CancellationToken cancellationToken)
    {
        try
        {
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
                CallerMode = sync ? ExecMode.Sync : ExecMode.Async,
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

            // Phase 1 is already committed; revert correlation in its own UoW so the
            // operation can be retried. parentInstance is detached (Phase 1 DbContext
            // disposed), but its in-memory state is correct for the revert call.
            await RevertCorrelationInNewUowAsync(parentInstance, subInstanceId, parentInstance.Id, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Completes the SubFlow correlation and persists the changes to the repository.
    /// Also resets the parent's EffectiveState back to its own CurrentState.
    /// </summary>
    private async Task CompleteAndPersistCorrelationAsync(
        Instance parentInstance,
        Guid subInstanceId,
        Guid parentInstanceId,
        CancellationToken cancellationToken)
    {
        var correlation = parentInstance.CompleteCorrelation(subInstanceId);
        if(correlation == null)
            return;

        // Reset parent's EffectiveState back to its own CurrentState
        // (SubFlow is now completed, so parent's state is no longer reflected from SubFlow)
        parentInstance.SetEffectiveState(parentInstance.GetCurrentState);

        logger.SubFlowCorrelationCompleted(subInstanceId, parentInstanceId);

        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
    }

    /// <summary>
    /// Reverts the SubFlow correlation in its own RequiresNew UoW and commits.
    /// Used when Phase 1 (correlation completion) succeeded but Phase 2 (pipeline resume) failed,
    /// so the correlation can be retried.
    /// </summary>
    private async Task RevertCorrelationInNewUowAsync(
        Instance parentInstance,
        Guid subInstanceId,
        Guid parentInstanceId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Transactional: DB-only revert (reload → revert correlation → persist), no remote call.
            // Required under SchemaSwitchingMode.TransactionLocal.
            await using var revertUow = uowManager.Begin(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });

            await RevertAndPersistCorrelationAsync(parentInstance, subInstanceId, parentInstanceId, cancellationToken);
            await revertUow.CommitAsync(cancellationToken);
        }
        catch (Exception revertEx)
        {
            // Log but don't throw — the original exception from ResumePipelineAsync takes priority
            logger.SubFlowCompletionFailed(revertEx, subInstanceId, parentInstanceId);
        }
    }

    /// <summary>
    /// Reverts the SubFlow correlation and persists the changes to the repository.
    /// </summary>
    private async Task RevertAndPersistCorrelationAsync(
        Instance parentInstance,
        Guid subInstanceId,
        Guid parentInstanceId,
        CancellationToken cancellationToken)
    {
        // S9 isolation rule: do NOT mutate the detached Phase-1 entity inside this new UoW —
        // reload by id so we operate on an entity tracked by the current scope's DbContext.
        // MUST load with ALL correlations: the default detail load filters completed
        // correlations out, which silently skipped the revert and left the parent stuck Busy.
        var tracked = await instanceRepository.FindWithAllCorrelationsAsync(parentInstanceId, cancellationToken)
                      ?? parentInstance;

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
