using System.Text.Json;
using BBT.Aether.Guids;
using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
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
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    IGuidGenerator guidGenerator,
    IWorkflowExecutionService workflowExecutionService,
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
                
                await using (var correlationUow = await uowManager.BeginAsync(
                    new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
                    cancellationToken))
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
                        // Commit correlation completion even if workflow load fails; skip pipeline resume
                        await correlationUow.CommitAsync(cancellationToken);
                        return;
                    }

                    parentWorkflow = parentWorkflowResult.Value!;

                    // Apply output mapping if configured (still within Phase 1 UoW)
                    var parentStateResult = parentWorkflow.GetState(correlation.ParentState);
                    if (parentStateResult.IsSuccess && parentStateResult.Value!.SubFlow?.Mapping != null)
                    {
                        logger.SubFlowOutputMappingStarted(parentInstance.Id);

                        var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                            .WithWorkflow(parentWorkflow)
                            .WithInstance(parentInstance)
                            .WithRuntime(runtimeInfoProvider)
                            .WithBody(completedInput.InstanceData?.Deserialize<Dictionary<string, object>>() ??
                                      new Dictionary<string, object>())
                            .BuildAsync(cancellationToken);

                        await ProcessSubFlowOutputMappingAsync(
                            parentInstance,
                            parentStateResult.Value!,
                            scriptContext,
                            cancellationToken);
                    }
                    
                    await correlationUow.CommitAsync(cancellationToken);
                }
                
                logger.SubFlowPipelineResumed(parentInstance.Id);
                await ResumePipelineAsync(
                    parentInstance,
                    parentWorkflow!,
                    completedInput.SubInstanceId,
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
    /// Processes SubFlow output mapping by executing the mapping script and merging results into parent instance data.
    /// </summary>
    private async Task ProcessSubFlowOutputMappingAsync(
        Instance parentInstance,
        State parentState,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var subFlowConfig = parentState.SubFlow;
            if (subFlowConfig == null)
            {
                return;
            }

            var mappingCode = subFlowConfig.Mapping.DecodedCode;

            // Compile the mapping script to the appropriate interface
            var mappingInstance = await scriptEngine.CompileToInstanceAsync<object>(
                mappingCode,
                cancellationToken: cancellationToken);

            ScriptResponse? outputMappingResult = null;

            // Execute OutputHandler for SubFlow (SubProcess doesn't have OutputHandler)
            if (subFlowConfig.Type.Equals(SubFlowType.SubFlow) && mappingInstance is ISubFlowMapping subFlowMapping)
            {
                outputMappingResult = await subFlowMapping.OutputHandler(scriptContext);
            }

            if (outputMappingResult?.Data != null)
            {
                // Add the mapped output data to the parent instance
                parentInstance.AddData(
                    guidGenerator.Create(),
                    new JsonData(JsonSerializer.Serialize(outputMappingResult.Data)),
                    parentState.VersionStrategy);

                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.SubFlowCompletionFailed(
                ex,
                Guid.Empty,
                parentInstance.Id);

            // Don't throw - we still want to resume automatic processes even if mapping fails
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
                Headers = new Dictionary<string, string?>(),
                Actor = ExecutionActor.System,
                RequestedAt = DateTimeOffset.UtcNow,
                Execution = new ExecutionInfo
                {
                    ExecutionChainId = Guid.NewGuid().ToString("N"),
                    ChainDepth = 0,
                    ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep,
                    IsSubFlowResume = true
                }
            };

            var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);
            
            if (!result.IsSuccess)
            {
                // Check if this is an auto-transition condition not met
                if (result.Error.Code != WorkflowErrorCodes.AutoTransitionConditionNotMet)
                {
                    logger.TransitionRuleFailed(
                        "subflow",
                        parentInstance.Id,
                        result.Error.Message ?? "Unknown error");
                }

                throw new SubflowCompletionException(
                    parentWorkflow.Domain,
                    parentWorkflow.Key,
                    parentInstance.Id.ToString(),
                    result.Error.Code,
                    result.Error.Message ?? "Unknown error"
                );
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
            await using var revertUow = await uowManager.BeginAsync(
                new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
                cancellationToken);

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
        var correlation = parentInstance.RevertCorrelation(subInstanceId);
        if(correlation == null)
            return;

        logger.SubFlowCorrelationReverted(subInstanceId, parentInstanceId);

        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
    }
}
