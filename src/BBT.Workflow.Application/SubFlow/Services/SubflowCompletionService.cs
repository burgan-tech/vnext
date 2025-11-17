using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Application.Services;
using BBT.Aether.Guids;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Schemas;
using BBT.Workflow.Telemetry;
using Microsoft.Extensions.Logging;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Shared;
using BBT.Workflow.Instances.Validation;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowCompletionService" />
public sealed class SubflowCompletionService(
    IServiceProvider serviceProvider,
    ICurrentSchema currentSchema,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceCorrelationRepository instanceCorrelationRepository,
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IRuntimeInfoProvider runtimeInfoProvider,
    IGuidGenerator guidGenerator,
    IWorkflowExecutionService workflowExecutionService,
    IInstanceDataValidationService instanceDataValidationService,
    ILogger<SubflowCompletionService> logger)
    : ApplicationService(serviceProvider), ISubflowCompletionService
{
    /// <inheritdoc />
    public async Task HandleSubFlowCompletionAsync(
        FlowCompletedDataEto completedDataEto,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        logger.LogInformation(
            "Processing SubFlow completion for instance {InstanceId} in domain {Domain}",
            completedDataEto.InstanceId, completedDataEto.Domain);

        // Parse parent information from metadata
        var subFlowContractInfo = completedDataEto.MetaData.ToSubFlowContractInfo();
        if (subFlowContractInfo == null)
        {
            logger.LogWarning(
                "No parent information found in metadata for completed SubFlow instance {InstanceId}. This may be a standalone flow.",
                completedDataEto.InstanceId);
            return;
        }
        
        // Enrich logs with parent workflow context for SubFlow completion
        using (logger.ForSubFlow(
                   parentDomain: subFlowContractInfo.Domain,
                   parentFlow: subFlowContractInfo.Flow,
                   parentFlowVersion: subFlowContractInfo.Version,
                   parentInstanceId: subFlowContractInfo.Id,
                   transitionKey: subFlowContractInfo.Transition))
        {
            // Check if this domain is handled by this runtime instance
            try
            {
                runtimeInfoProvider.Check(subFlowContractInfo.Domain);
            }
            catch (NotFoundDomainException)
            {
                logger.LogInformation(
                    "SubFlow completion event for instance {InstanceId} belongs to domain {Domain} which is not handled by this runtime instance {RuntimeDomain}. Event will be ignored.",
                    completedDataEto.InstanceId, subFlowContractInfo.Domain, runtimeInfoProvider.Domain);
                return; // Silently ignore - this is expected in multi-domain scenarios
            }

            logger.LogDebug(
                "SubFlow completion event belongs to parent instance {ParentInstanceId} in domain {Domain}, flow {Flow}",
                subFlowContractInfo.Id, subFlowContractInfo.Domain, subFlowContractInfo.Flow);

            // Create span for SubFlow completion
            using var activity = WorkflowActivitySource.Instance.StartActivity(
                TelemetryConstants.SpanNames.SubFlowComplete);

            activity?.SetTag(TelemetryConstants.TagNames.SubFlowKey, subFlowContractInfo.Flow);
            activity?.SetTag(TelemetryConstants.TagNames.Domain, subFlowContractInfo.Domain);
            activity?.SetTag(TelemetryConstants.TagNames.InstanceId, completedDataEto.InstanceId.ToString());
            activity?.SetDisplayName($"SubFlow Complete: {subFlowContractInfo.Flow}");

            try
            {
                using (currentSchema.Change(subFlowContractInfo.Flow))
                {
                    // TODO: Bunu instance içinde yönet.
                    // Find the correlation record for this completed SubFlow
                    var correlation = await instanceCorrelationRepository
                        .FindBySubInstanceIdAsync(completedDataEto.InstanceId, cancellationToken);

                    if (correlation == null)
                    {
                        logger.LogWarning(
                            "No correlation found for completed SubFlow instance {InstanceId}. This may be a standalone flow or already processed.",
                            completedDataEto.InstanceId);
                        return;
                    }

                    if (correlation.IsCompleted)
                    {
                        logger.LogWarning(
                            "SubFlow correlation {CorrelationId} for instance {InstanceId} is already marked as completed.",
                            correlation.Id, completedDataEto.InstanceId);
                        return;
                    }

                    logger.LogInformation(
                        "Found SubFlow correlation {CorrelationId} for parent instance {ParentInstanceId} in state {ParentState}",
                        correlation.Id, correlation.ParentInstanceId, correlation.ParentState);

                    // Mark the correlation as completed
                    correlation.Completed();
                    await instanceCorrelationRepository.UpdateAsync(correlation, true, cancellationToken);

                    if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
                    {
                        logger.LogInformation(
                            "SubProcess {SubFlowName} for instance {InstanceId} has completed",
                            correlation.SubFlowName, completedDataEto.InstanceId);
                        return;
                    }

                    // Process parent workflow continuation within the parent's schema context
                    await ProcessParentWorkflowContinuationAsync(correlation, completedDataEto, subFlowContractInfo,
                        cancellationToken);

                    sw.Stop();

                    // Log SubFlow completion
                    logger.SubFlowCompleted(
                        TelemetryConstants.Prefixes.Execution,
                        correlation.SubFlowName,
                        completedDataEto.InstanceId,
                        sw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();

                activity?.RecordExceptionWithStatus(ex);

                logger.LogError(ex,
                    "Failed to process SubFlow completion for instance {InstanceId}",
                    completedDataEto.InstanceId);

                throw;
            }
        }
    }

    /// <summary>
    /// Processes the parent workflow continuation after SubFlow completion.
    /// This includes output mapping and resuming automatic/scheduled transitions.
    /// </summary>
    /// <param name="correlation">The SubFlow correlation</param>
    /// <param name="completedDataEto">The completed SubFlow data</param>
    /// <param name="subFlowContractInfo">The parsed parent workflow information</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ProcessParentWorkflowContinuationAsync(
        InstanceCorrelation correlation,
        FlowCompletedDataEto completedDataEto,
        SubFlowContractInfo subFlowContractInfo,
        CancellationToken cancellationToken)
    {
        // Get the parent instance
        var parentInstance = await instanceRepository.FindAsync(correlation.ParentInstanceId, true, cancellationToken);

        if (parentInstance == null)
        {
            logger.LogWarning(
                "Parent instance {ParentInstanceId} not found for correlation {CorrelationId}",
                correlation.ParentInstanceId, correlation.Id);
            return;
        }

        logger.LogInformation(
            "Processing parent workflow continuation for instance {ParentInstanceId} in state {CurrentState}",
            parentInstance.Id, parentInstance.CurrentState);

        // Get parent workflow definition
        var parentWorkflow = await componentCacheStore.GetFlowAsync(
            subFlowContractInfo.Domain,
            subFlowContractInfo.Flow,
            subFlowContractInfo.Version, // Use the specific version from parent info
            cancellationToken);

        // Get the state where SubFlow was initiated using Result Pattern
        var parentStateResult = parentWorkflow.GetState(correlation.ParentState);
        if (!parentStateResult.IsSuccess)
        {
            logger.LogError(
                "Parent state {ParentState} not found in workflow {WorkflowKey} for SubFlow completion. Skipping parent continuation.",
                correlation.ParentState, parentWorkflow.Key);
            return;
        }

        var parentState = parentStateResult.Value!;
        var transition = parentWorkflow.FindTransitionInContext(subFlowContractInfo.Transition!);

        // Create script context for output mapping with completed SubFlow data
        var scriptContextBuilder = scriptContextFactory.NewBuilder()
            .WithWorkflow(parentWorkflow)
            .WithInstance(parentInstance)
            .WithRuntime(runtimeInfoProvider)
            .WithBody(completedDataEto.InstanceData?.Deserialize<Dictionary<string, object>>() ??
                      new Dictionary<string, object>());

        if (parentState.SubFlow?.Mapping != null)
        {
            logger.LogInformation(
                "Processing SubFlow output mapping for parent instance {ParentInstanceId}",
                parentInstance.Id);

            await ProcessSubFlowOutputMappingAsync(
                parentInstance,
                parentWorkflow,
                parentState,
                await scriptContextBuilder.BuildAsync(cancellationToken),
                cancellationToken);
        }

        // Resume automatic transitions and scheduled processes that were paused for SubFlow
        logger.LogInformation(
            "Resuming automatic transitions for parent instance {ParentInstanceId} after SubFlow completion",
            parentInstance.Id);

        await ResumePipelineAsync(
            parentInstance,
            parentWorkflow,
            transition!,
            cancellationToken);
    }

    /// <summary>
    /// Processes SubFlow output mapping by executing the mapping script and merging results into parent instance data.
    /// </summary>
    /// <param name="parentInstance">The parent workflow instance</param>
    /// <param name="parentWorkflow">The parent workflow definition</param>
    /// <param name="parentState">The parent state containing SubFlow configuration</param>
    /// <param name="scriptContext">The script context for mapping execution</param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ProcessSubFlowOutputMappingAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        State parentState,
        ScriptContext scriptContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var subFlowConfig = parentState.SubFlow;
            if (subFlowConfig == null)
            {
                logger.LogWarning(
                    "SubFlow configuration not found for parent instance {ParentInstanceId}",
                    parentInstance.Id);
                return;
            }

            var mappingCode = subFlowConfig.Mapping.DecodedCode;

            logger.LogDebug(
                "Executing SubFlow output mapping for parent instance {ParentInstanceId}",
                parentInstance.Id);

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
                logger.LogInformation(
                    "Merging SubFlow output data into parent instance {ParentInstanceId}",
                    parentInstance.Id);

                // Add the mapped output data to the parent instance
                parentInstance.AddData(
                    guidGenerator.Create(),
                    new JsonData(JsonSerializer.Serialize(outputMappingResult.Data)),
                    parentState.VersionStrategy);
                var validationResult = await instanceDataValidationService.ValidateOnDataChangeAsync(
                    parentInstance,
                    parentWorkflow,
                    logger,
                    cancellationToken);
 
                if (!validationResult.IsSuccess)
                {
                    throw new InvalidOperationException(
                        $"Instance data validation failed after SubFlow output mapping: {validationResult.Error.Message}");
                }
                await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);

                logger.LogDebug(
                    "Successfully merged SubFlow output data into parent instance {ParentInstanceId}",
                    parentInstance.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to process SubFlow output mapping for parent instance {ParentInstanceId}",
                parentInstance.Id);

            // Don't throw - we still want to resume automatic processes even if mapping fails
        }
    }

    /// <summary>
    /// Resumes automatic transitions and scheduled processes for the parent workflow after SubFlow completion.
    /// This continues the workflow execution that was paused while waiting for SubFlow completion.
    /// </summary>
    /// <param name="parentInstance">The parent workflow instance</param>
    /// <param name="parentWorkflow">The parent workflow definition</param>
    /// <param name="transition"></param>
    /// <param name="cancellationToken">Cancellation token</param>
    private async Task ResumePipelineAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        Transition transition,
        CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation(
                "Resuming automatic processes for parent instance {ParentInstanceId} in state {CurrentState}",
                parentInstance.Id, parentInstance.CurrentState);

            // using var scope = ServiceProvider.CreateScope();
            // var workflowExecutionService = scope.ServiceProvider.GetRequiredService<IWorkflowExecutionService>();

            var input = new WorkflowExecutionContext
            {
                Domain = parentWorkflow.Domain,
                WorkflowKey = parentWorkflow.Key,
                WorkflowVersion = parentWorkflow.Version,
                InstanceId = parentInstance.Id,
                TransitionKey = transition.Key, // For logging purposes only
                TriggerType = transition.TriggerType,
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
                if (result.Error.Code == WorkflowErrorCodes.AutoTransitionConditionNotMet)
                {
                    logger.LogDebug(
                        "Auto-transition condition not met for parent instance {ParentInstanceId} after SubFlow completion - this is normal",
                        parentInstance.Id);
                }
                else
                {
                    logger.LogWarning(
                        "Failed to resume automatic processes for parent instance {ParentInstanceId}: {ErrorCode} - {ErrorMessage}",
                        parentInstance.Id, result.Error.Code, result.Error.Message);
                }

                // Don't throw - SubFlow completion should still be marked as successful
                return;
            }

            logger.LogInformation(
                "Successfully resumed automatic processes for parent instance {ParentInstanceId}",
                parentInstance.Id);
        }
        catch (AutoTransitionConditionNotMetException)
        {
            logger.LogDebug(
                "Auto-transition condition not met for parent instance {ParentInstanceId} after SubFlow completion - this is normal",
                parentInstance.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to resume automatic processes for parent instance {ParentInstanceId}",
                parentInstance.Id);

            // Don't throw - SubFlow completion should still be marked as successful
        }
    }
}