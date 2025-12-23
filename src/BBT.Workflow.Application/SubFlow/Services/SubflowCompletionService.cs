using System.Text.Json;
using BBT.Aether.Application.Services;
using BBT.Aether.Guids;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
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
    IServiceProvider serviceProvider,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    IGuidGenerator guidGenerator,
    IWorkflowExecutionService workflowExecutionService,
    IErrorPolicyResolver errorPolicyResolver,
    IErrorActionExecutor errorActionExecutor,
    ILogger<SubflowCompletionService> logger)
    : ApplicationService(serviceProvider), ISubflowCompletionService
{
    /// <inheritdoc />
    public async Task CompletionAsync(
        FlowCompletedInput completedInput,
        CancellationToken cancellationToken = default)
    {
        // Check if this domain is handled by this runtime instance
        try
        {
            runtimeInfoProvider.Check(completedInput.Domain);
        }
        catch (NotFoundDomainException)
        {
            // Silently ignore - this is expected in multi-domain scenarios
            return;
        }

        try
        {
            // Get parent instance
            var parentInstance = await instanceRepository.FindAsync(
                completedInput.InstanceId, true, cancellationToken);

            if (parentInstance == null)
            {
                logger.InstanceNotFound(
                    completedInput.InstanceId,
                    completedInput.Flow);
                return;
            }

            // Complete correlation through Instance aggregate
            var correlation = parentInstance.CompleteCorrelation(completedInput.SubInstanceId);
            if (correlation == null)
            {
                logger.SubFlowCorrelationNotFound(completedInput.SubInstanceId);
                return;
            }

            logger.SubFlowCorrelationCompleted(
                completedInput.SubInstanceId,
                completedInput.InstanceId);

            // Save instance (correlation is part of aggregate)
            await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);

            // If this is a SubProcess (non-blocking), just return after marking correlation as completed
            if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
            {
                return;
            }

            // Check if SubFlow completed with an error
            if (completedInput.HasError && completedInput.ErrorPayload != null)
            {
                await ProcessSubFlowErrorAsync(
                    correlation,
                    completedInput,
                    parentInstance,
                    cancellationToken);

                // Cleanup cached error context from ExtraProperties
                await CleanupSubFlowErrorContextAsync(parentInstance, correlation.Id, cancellationToken);
                return;
            }

            // Process parent workflow continuation for SubFlow (blocking)
            await ProcessParentWorkflowContinuationAsync(
                correlation,
                completedInput,
                parentInstance,
                cancellationToken);

            // Cleanup cached error context from ExtraProperties (even on success)
            await CleanupSubFlowErrorContextAsync(parentInstance, correlation.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.SubFlowCompletionFailed(
                ex,
                completedInput.SubInstanceId,
                completedInput.InstanceId);
        }
    }

    /// <summary>
    /// Processes the parent workflow continuation after SubFlow completion.
    /// This includes output mapping and resuming automatic/scheduled transitions.
    /// </summary>
    /// <param name="correlation">The SubFlow correlation</param>
    /// <param name="completedInput">The completed SubFlow data</param>
    /// <param name="parentInstance">The parent instance</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ProcessParentWorkflowContinuationAsync(
        InstanceCorrelation correlation,
        FlowCompletedInput completedInput,
        Instance parentInstance,
        CancellationToken cancellationToken)
    {
        logger.SubFlowParentContinuationStarted(
            parentInstance.Id,
            parentInstance.GetCurrentState);

        // Get parent workflow definition using data from completedInput
        var parentWorkflowResult = await componentCacheStore.GetFlowAsync(
            completedInput.Domain,
            completedInput.Flow,
            completedInput.Version,
            cancellationToken);

        if (!parentWorkflowResult.IsSuccess)
        {
            logger.LogWarning("Failed to get parent workflow {Flow} for SubFlow completion: {ErrorCode}",
                completedInput.Flow, parentWorkflowResult.Error.Code);
            return;
        }

        var parentWorkflow = parentWorkflowResult.Value!;

        // Get the state where SubFlow was initiated using Result Pattern
        var parentStateResult = parentWorkflow.GetState(correlation.ParentState);
        if (!parentStateResult.IsSuccess)
        {
            // Log error using WorkflowErrors - state not found
            return;
        }

        var parentState = parentStateResult.Value!;

        // Create script context for output mapping with completed SubFlow data
        var scriptContextBuilder = scriptContextFactory.NewBuilder(instanceRepository)
            .WithWorkflow(parentWorkflow)
            .WithInstance(parentInstance)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(completedInput.InstanceData?.Deserialize<Dictionary<string, object>>() ??
                      new Dictionary<string, object>());

        if (parentState.SubFlow?.Mapping != null)
        {
            logger.SubFlowOutputMappingStarted(parentInstance.Id);

            await ProcessSubFlowOutputMappingAsync(
                parentInstance,
                parentState,
                await scriptContextBuilder.BuildAsync(cancellationToken),
                cancellationToken);
        }

        // Resume automatic transitions and scheduled processes that were paused for SubFlow
        logger.SubFlowPipelineResumed(parentInstance.Id);

        await ResumePipelineAsync(
            parentInstance,
            parentWorkflow,
            cancellationToken);
    }

    /// <summary>
    /// Processes SubFlow output mapping by executing the mapping script and merging results into parent instance data.
    /// </summary>
    /// <param name="parentInstance">The parent workflow instance</param>
    /// <param name="parentState">The parent state containing SubFlow configuration</param>
    /// <param name="scriptContext"></param>
    /// <param name="cancellationToken">Cancellation token</param>
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
    /// Processes SubFlow error using the error boundary system.
    /// First attempts to use cached error context from ExtraProperties (set by HandleSubFlowStep),
    /// then falls back to loading workflow definition if needed for policy resolution.
    /// </summary>
    private async Task ProcessSubFlowErrorAsync(
        InstanceCorrelation correlation,
        FlowCompletedInput completedInput,
        Instance parentInstance,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogWarning(
                "Processing SubFlow error from child {ChildInstanceId} for parent {ParentInstanceId}",
                completedInput.SubInstanceId,
                parentInstance.Id);

            var errorPayload = completedInput.ErrorPayload!;

            // Try to get error context from ExtraProperties (cached by HandleSubFlowStep)
            var errorContextKey = $"SubFlowErrorContext:{correlation.Id}";
            var cachedContext = TryGetCachedErrorContext(parentInstance, errorContextKey);

            if (cachedContext != null)
            {
                // Use cached context - more performant, avoids workflow definition reload
                await ProcessSubFlowErrorWithCachedContextAsync(
                    cachedContext,
                    correlation,
                    completedInput,
                    parentInstance,
                    errorPayload,
                    cancellationToken);
                return;
            }

            // Fallback: Load workflow definition (for backward compatibility or if context not cached)
            logger.LogDebug(
                "Cached error context not found for correlation {CorrelationId}, loading workflow definition",
                correlation.Id);

            await ProcessSubFlowErrorWithWorkflowDefinitionAsync(
                correlation,
                completedInput,
                parentInstance,
                errorPayload,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error processing SubFlow error for parent {ParentInstanceId}",
                parentInstance.Id);
        }
    }

    /// <summary>
    /// Attempts to retrieve cached SubFlow error context from instance ExtraProperties.
    /// </summary>
    private static SubFlowCachedErrorContext? TryGetCachedErrorContext(
        Instance parentInstance,
        string errorContextKey)
    {
        if (!parentInstance.ExtraProperties.TryGetValue(errorContextKey, out var storedContext))
        {
            return null;
        }

        // Handle both Dictionary<string, object?> and JsonElement cases
        if (storedContext is Dictionary<string, object?> dictContext)
        {
            return SubFlowCachedErrorContext.FromDictionary(dictContext);
        }

        if (storedContext is JsonElement jsonElement)
        {
            return SubFlowCachedErrorContext.FromJsonElement(jsonElement);
        }

        return null;
    }

    /// <summary>
    /// Processes SubFlow error using cached context from ExtraProperties.
    /// This is the optimized path that avoids reloading workflow definition.
    /// </summary>
    private async Task ProcessSubFlowErrorWithCachedContextAsync(
        SubFlowCachedErrorContext cachedContext,
        InstanceCorrelation correlation,
        FlowCompletedInput completedInput,
        Instance parentInstance,
        SubFlowErrorPayload errorPayload,
        CancellationToken cancellationToken)
    {
        logger.LogDebug(
            "Using cached error context for SubFlow error handling. PropagateToParent: {Propagate}",
            cachedContext.PropagateToParent);

        // Check if error should be propagated to parent
        if (!cachedContext.PropagateToParent)
        {
            logger.LogInformation(
                "SubFlow error not propagated to parent (PropagateToParent=false)");

            // Still resume the pipeline, but the error is not propagated
            // For cached context, we need to load workflow for resume (minimal overhead)
            var workflowForResume = await LoadWorkflowForResumeAsync(completedInput, cancellationToken);
            if (workflowForResume != null)
            {
                await ResumePipelineAsync(parentInstance, workflowForResume, cancellationToken);
            }
            return;
        }

        // If propagation is needed but we need full policy resolution,
        // we still need workflow definition for error boundary rules
        if (cachedContext.HasErrorBoundary)
        {
            // Has custom error boundary - need to load workflow for policy resolution
            await ProcessSubFlowErrorWithWorkflowDefinitionAsync(
                correlation,
                completedInput,
                parentInstance,
                errorPayload,
                cancellationToken);
            return;
        }

        // No custom error boundary - use ParentTransition if configured
        if (!string.IsNullOrEmpty(cachedContext.ParentTransition))
        {
            logger.LogInformation(
                "Using cached ParentTransition: {Transition}",
                cachedContext.ParentTransition);

            var workflowForTransition = await LoadWorkflowForResumeAsync(completedInput, cancellationToken);
            if (workflowForTransition != null)
            {
                await TriggerErrorTransitionAsync(
                    parentInstance,
                    workflowForTransition,
                    cachedContext.ParentTransition,
                    cancellationToken);
            }
            return;
        }

        // No handling configured - log and let it bubble up
        logger.LogError(
            "No error policy found for SubFlow error in state {State}",
            cachedContext.ParentStateKey);
    }

    /// <summary>
    /// Processes SubFlow error by loading workflow definition.
    /// This is the fallback path for full policy resolution.
    /// </summary>
    private async Task ProcessSubFlowErrorWithWorkflowDefinitionAsync(
        InstanceCorrelation correlation,
        FlowCompletedInput completedInput,
        Instance parentInstance,
        SubFlowErrorPayload errorPayload,
        CancellationToken cancellationToken)
    {
        // Get parent workflow definition
        var parentWorkflowResult = await componentCacheStore.GetFlowAsync(
            completedInput.Domain,
            completedInput.Flow,
            completedInput.Version,
            cancellationToken);

        if (!parentWorkflowResult.IsSuccess)
        {
            logger.LogError(
                "Failed to get parent workflow {Flow} for SubFlow error handling",
                completedInput.Flow);
            return;
        }

        var parentWorkflow = parentWorkflowResult.Value!;

        // Get the SubFlow state
        var subFlowStateResult = parentWorkflow.GetState(correlation.ParentState);
        if (!subFlowStateResult.IsSuccess)
        {
            logger.LogError(
                "SubFlow state {State} not found in parent workflow",
                correlation.ParentState);
            return;
        }

        var subFlowState = subFlowStateResult.Value!;

        // Check SubFlow error policy - get from SubFlow configuration
        if (subFlowState.SubFlow == null)
        {
            logger.LogError(
                "SubFlow configuration not found in state {State}",
                subFlowState.Key);
            return;
        }

        var subFlowPolicy = subFlowState.SubFlow.EffectiveErrorPolicy;

        if (!subFlowPolicy.PropagateToParent)
        {
            logger.LogInformation(
                "SubFlow error not propagated to parent (PropagateToParent=false)");

            // Still resume the pipeline, but the error is not propagated
            await ResumePipelineAsync(parentInstance, parentWorkflow, cancellationToken);
            return;
        }

        // Build error context for parent
        var errorContext = new ErrorContext
        {
            ExceptionTypeName = errorPayload.ExceptionTypeName,
            ErrorCode = errorPayload.ErrorCode,
            IsTimeout = errorPayload.IsTimeout,
            Scope = ErrorBoundaryScope.SubFlow,
            TaskKey = null,
            StateKey = subFlowState.Key,
            TransitionKey = null,
            InstanceId = parentInstance.Id,
            Domain = completedInput.Domain,
            Flow = completedInput.Flow,
            Attempt = 0,
            Message = subFlowPolicy.IncludeChildErrorDetails
                ? errorPayload.Message
                : "SubFlow completed with error",
            Details = subFlowPolicy.IncludeChildErrorDetails
                ? errorPayload.Details
                : null,
            OccurredAt = errorPayload.OccurredAt
        };

        // Build a minimal transition context for policy resolution
        var transitionContext = new TransitionExecutionContext
        {
            Domain = completedInput.Domain,
            InstanceId = parentInstance.Id,
            WorkflowKey = completedInput.Flow,
            Instance = parentInstance,
            Workflow = parentWorkflow,
            Current = subFlowState
        };

        // Resolve error policy
        var resolvedPolicy = errorPolicyResolver.ResolveSubFlow(
            transitionContext,
            errorContext,
            subFlowState);

        if (resolvedPolicy == null)
        {
            // No policy found - check if there's a ParentTransition configured
            if (!string.IsNullOrEmpty(subFlowPolicy.ParentTransition))
            {
                logger.LogInformation(
                    "No error policy found, using SubFlowErrorPolicy.ParentTransition: {Transition}",
                    subFlowPolicy.ParentTransition);

                await TriggerErrorTransitionAsync(
                    parentInstance,
                    parentWorkflow,
                    subFlowPolicy.ParentTransition,
                    cancellationToken);
                return;
            }

            // No handling configured - log and let it bubble up
            logger.LogError(
                "No error policy found for SubFlow error in state {State}",
                subFlowState.Key);
            return;
        }

        // Execute the error action
        var actionResult = await errorActionExecutor.ExecuteAsync(
            transitionContext,
            errorContext,
            resolvedPolicy,
            cancellationToken);

        // Handle the action result
        if (actionResult.HasTransition)
        {
            await TriggerErrorTransitionAsync(
                parentInstance,
                parentWorkflow,
                actionResult.TransitionKey!,
                cancellationToken);
        }
        else if (actionResult.ShouldContinue)
        {
            // Action allows continuation (e.g., Ignore)
            await ResumePipelineAsync(parentInstance, parentWorkflow, cancellationToken);
        }
        // If neither, the workflow stays in current state (e.g., Abort without transition)
    }

    /// <summary>
    /// Loads workflow definition for resume operations.
    /// </summary>
    private async Task<Definitions.Workflow?> LoadWorkflowForResumeAsync(
        FlowCompletedInput completedInput,
        CancellationToken cancellationToken)
    {
        var workflowResult = await componentCacheStore.GetFlowAsync(
            completedInput.Domain,
            completedInput.Flow,
            completedInput.Version,
            cancellationToken);

        if (!workflowResult.IsSuccess)
        {
            logger.LogError(
                "Failed to load workflow {Flow} for resume operation",
                completedInput.Flow);
            return null;
        }

        return workflowResult.Value;
    }

    /// <summary>
    /// Triggers an error transition on the parent workflow.
    /// </summary>
    private async Task TriggerErrorTransitionAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string transitionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Triggering error transition {Transition} for instance {InstanceId}",
                transitionKey,
                parentInstance.Id);

            var input = new WorkflowExecutionContext
            {
                Domain = parentWorkflow.Domain,
                WorkflowKey = parentWorkflow.Key,
                WorkflowVersion = parentWorkflow.Version,
                InstanceId = parentInstance.Id.ToString(),
                TransitionKey = transitionKey,
                TriggerType = TriggerType.Automatic,
                Mode = ExecMode.Sync,
                Headers = new Dictionary<string, string?>(),
                Actor = ExecutionActor.System,
                RequestedAt = DateTimeOffset.UtcNow,
                Execution = new ExecutionInfo
                {
                    ExecutionChainId = Guid.NewGuid().ToString("N"),
                    ChainDepth = 0
                }
            };

            var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);

            if (!result.IsSuccess)
            {
                logger.LogError(
                    "Failed to execute error transition {Transition}: {Error}",
                    transitionKey,
                    result.Error.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error triggering error transition {Transition} for instance {InstanceId}",
                transitionKey,
                parentInstance.Id);
        }
    }

    /// <summary>
    /// Resumes automatic transitions and scheduled processes for the parent workflow after SubFlow completion.
    /// This continues the workflow execution that was paused while waiting for SubFlow completion.
    /// </summary>
    /// <param name="parentInstance">The parent workflow instance</param>
    /// <param name="parentWorkflow">The parent workflow definition</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ResumePipelineAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
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
            }
        }
        catch (Exception ex)
        {
            logger.SubFlowCompletionFailed(
                ex,
                Guid.Empty,
                parentInstance.Id);

            // Don't throw - SubFlow completion should still be marked as successful
        }
    }

    /// <summary>
    /// Cleans up the cached SubFlow error context from instance ExtraProperties.
    /// This should be called after SubFlow completion (both success and error cases)
    /// to prevent data pollution in the instance.
    /// </summary>
    /// <param name="parentInstance">The parent instance</param>
    /// <param name="correlationId">The SubFlow correlation ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task CleanupSubFlowErrorContextAsync(
        Instance parentInstance,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        try
        {
            var errorContextKey = $"SubFlowErrorContext:{correlationId}";

            if (parentInstance.ExtraProperties.ContainsKey(errorContextKey))
            {
                parentInstance.ExtraProperties.Remove(errorContextKey);
                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);

                logger.LogDebug(
                    "Cleaned up SubFlow error context for correlation {CorrelationId}",
                    correlationId);
            }
        }
        catch (Exception ex)
        {
            // Log but don't throw - cleanup failure shouldn't affect SubFlow completion
            logger.LogWarning(ex,
                "Failed to cleanup SubFlow error context for correlation {CorrelationId}",
                correlationId);
        }
    }
}