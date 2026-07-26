using BBT.Aether.Uow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.SubFlow;

/// <inheritdoc cref="ISubflowFaultService" />
public sealed class SubflowFaultService(
    IUnitOfWorkManager uowManager,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IWorkflowExecutionService workflowExecutionService,
    ISubflowOutputMappingService outputMappingService,
    IErrorBoundaryResolver errorBoundaryResolver,
    IErrorActionExecutor errorActionExecutor,
    ITransitionLockScopeFactory transitionLockScopeFactory,
    ILogger<SubflowFaultService> logger)
    : ISubflowFaultService
{
    /// <inheritdoc />
    public async Task FaultAsync(
        SubFlowFaultedInput input,
        CancellationToken cancellationToken = default)
    {
        using var activity = SubFlowActivityHelper.StartActivity($"SubFlow.Fault/{input.Domain}/{input.Flow}");
        SubFlowActivityHelper.EnrichWithCompletion(
            activity,
            input.SubInstanceId,
            input.InstanceId,
            input.Domain,
            input.Flow);
        activity?.SetTag(TelemetryConstants.TagNames.FlowVersion, input.Version ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.RootInstanceId, input.RootInstanceId?.ToString() ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, input.InstanceId.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.SubItemType, input.SubItemType.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.SubItemOutcome, SubItemTerminalOutcome.Faulted.ToString());
        activity?.SetTag(TelemetryConstants.TagNames.TerminationOrigin, input.Termination?.Origin.ToString() ?? "legacy");
        activity?.SetTag(TelemetryConstants.TagNames.TerminationInitiator, input.Termination?.InitiatorInstanceId.ToString() ?? "N/A");
        activity?.SetTag(TelemetryConstants.TagNames.TerminationCascadeId, input.Termination?.CascadeId.ToString() ?? "N/A");

        var scopeProperties = new Dictionary<string, object>
        {
            [TelemetryConstants.TagNames.Domain] = input.Domain,
            [TelemetryConstants.TagNames.Flow] = input.Flow,
            [TelemetryConstants.TagNames.FlowVersion] = input.Version ?? "N/A",
            [TelemetryConstants.TagNames.InstanceId] = input.InstanceId,
            [TelemetryConstants.TagNames.RootInstanceId] = input.RootInstanceId?.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.ParentInstanceId] = input.InstanceId,
            [TelemetryConstants.TagNames.SubflowInstanceId] = input.SubInstanceId,
            [TelemetryConstants.TagNames.SubItemType] = input.SubItemType.ToString(),
            [TelemetryConstants.TagNames.SubItemOutcome] = SubItemTerminalOutcome.Faulted.ToString(),
            [TelemetryConstants.TagNames.TerminationOrigin] = input.Termination?.Origin.ToString() ?? "legacy",
            [TelemetryConstants.TagNames.TerminationInitiator] = input.Termination?.InitiatorInstanceId.ToString() ?? "N/A",
            [TelemetryConstants.TagNames.TerminationCascadeId] = input.Termination?.CascadeId.ToString() ?? "N/A"
        };
        using (logger.BeginScope(scopeProperties))
        {
            try
            {
                Instance? parentInstance;
                Definitions.Workflow? parentWorkflow = null;
                InstanceCorrelation? correlation;
                ActionExecutionResult? actionResult = null;

                // Per-subInstance terminal lock, independent of the main-flow lock and reserved keys:
                // a long-held chain lease never blocks the signal, parallel SubProcess terminal-closes
                // don't contend, and only duplicate deliveries of the SAME subInstance serialize.
                // Sync/async safe: distinct from the parent's held key (no self-deadlock); nested
                // same-key reentry is still handled by ChainLockRegistry.
                var lockKey = $"vnext:{input.Domain}:{input.Flow}:{input.InstanceId}:sub:{input.SubInstanceId:N}";
                await using (var lockScope = await transitionLockScopeFactory.AcquireAsync(lockKey, cancellationToken))
                {
                    if (!lockScope.IsAcquired)
                    {
                        logger.SubItemTerminalLockNotAcquired(lockKey, SubItemTerminalOutcome.Faulted.ToString());
                        throw new SubflowTerminalLockNotAcquiredException(
                            input.Domain,
                            input.Flow,
                            input.InstanceId.ToString(),
                            SubItemTerminalOutcome.Faulted.ToString());
                    }

                    await using var uow = uowManager.Begin(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew });
                    // Data must be loaded here: output mapping appends a new data version via
                    // Instance.AddData, whose version/IsLatest math reads the in-memory data list.
                    parentInstance = await instanceRepository.FindWithAllCorrelationsAndDataAsync(
                        input.InstanceId, cancellationToken);

                    if (parentInstance == null)
                    {
                        logger.InstanceNotFound(input.InstanceId, input.Flow);
                        activity?.SetTag("vnext.subflow.result", "parent_not_found");
                        return;
                    }

                    // Verify the correlation exists and apply terminal idempotency semantics.
                    correlation = parentInstance.FindCorrelationBySubInstanceId(input.SubInstanceId);
                    if (correlation == null)
                    {
                        logger.SubFlowCorrelationNotFound(input.SubInstanceId);
                        activity?.SetTag("vnext.subflow.result", "correlation_not_found");
                        await uow.CommitAsync(cancellationToken);
                        return;
                    }

                    activity?.SetTag(TelemetryConstants.TagNames.SubItemType, correlation.SubFlowType.Code);
                    scopeProperties[TelemetryConstants.TagNames.SubItemType] = correlation.SubFlowType.Code;

                    if (correlation.IsCompleted)
                    {
                        if (correlation.TerminalOutcome == SubItemTerminalOutcome.Faulted)
                        {
                            logger.SubItemTerminalDuplicate(
                                SubItemTerminalOutcome.Faulted.ToString(),
                                input.InstanceId,
                                input.SubInstanceId);
                        }
                        else
                        {
                            logger.SubItemTerminalConflict(
                                input.InstanceId,
                                input.SubInstanceId,
                                correlation.TerminalOutcome?.ToString() ?? "legacy",
                                SubItemTerminalOutcome.Faulted.ToString());
                        }

                        activity?.SetTag("vnext.subflow.result", "correlation_already_terminal");
                        await uow.CommitAsync(cancellationToken);
                        return;
                    }

                    // A terminal parent still needs an active SubProcess correlation closed, but
                    // a blocking SubFlow must not mutate or resume an already-terminal parent.
                    if (parentInstance.IsCompleted &&
                        correlation.SubFlowType.Equals(SubFlowType.SubFlow))
                    {
                        activity?.SetTag("vnext.subflow.result", "parent_already_terminal");
                        await uow.CommitAsync(cancellationToken);
                        return;
                    }

                    correlation.UpdateSubFlowState(input.FaultedState, input.FaultedAt);
                    parentInstance.CompleteCorrelation(
                        input.SubInstanceId,
                        SubItemTerminalOutcome.Faulted,
                        input.FaultedAt);

                    if (correlation.SubFlowType.Equals(SubFlowType.SubProcess))
                    {
                        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
                        await uow.CommitAsync(cancellationToken);
                        return;
                    }

                    parentInstance.SetEffectiveState(parentInstance.GetCurrentState);

                    var parentWorkflowResult = await componentCacheStore.GetFlowAsync(
                        input.Domain,
                        input.Flow,
                        input.Version,
                        cancellationToken);

                    if (!parentWorkflowResult.IsSuccess)
                    {
                        RecordIncident(parentInstance, input, ErrorAction.Abort, null);
                        parentInstance.Fault(input.Domain, input.Sync);
                        await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
                        await uow.CommitAsync(cancellationToken);
                        return;
                    }

                    parentWorkflow = parentWorkflowResult.Value!;

                    var executionError = BuildExecutionError(input);
                    var currentState = parentWorkflow.GetState(parentInstance.GetCurrentState).Value;
                    var resolution = errorBoundaryResolver.Resolve(
                        executionError.NormalizedError,
                        taskBoundary: null,
                        stateBoundary: currentState?.ErrorBoundary,
                        globalBoundary: parentWorkflow.ErrorBoundary);

                    actionResult = await errorActionExecutor.ExecuteAsync(
                        resolution,
                        executionError,
                        retryExecutor: null,
                        cancellationToken);

                    // Record the incident on the parent BEFORE running output mapping so the
                    // subflow fault (including stack trace) is visible to the output-mapping
                    // script via ScriptContext.Incident, enabling error-driven routing.
                    var incident = RecordIncident(
                        parentInstance,
                        input,
                        actionResult.ExecutedAction,
                        actionResult.ResolvedAtLevel);

                    if (actionResult.ShouldContinue)
                    {
                        incident.Resolve();
                    }
                    else if (string.IsNullOrWhiteSpace(actionResult.TransitionKey))
                    {
                        parentInstance.Fault(input.Domain, input.Sync);
                    }

                    var mappingResult = await outputMappingService.ApplyAsync(
                        parentInstance,
                        parentWorkflow,
                        correlation.ParentState,
                        input.InstanceData,
                        cancellationToken);

                    // Output mapping failure is non-blocking here: the instance is already
                    // marked Faulted/transitioned above. Just log and proceed so the fault
                    // is committed and propagated upward via InstanceSubFaultedEvent.
                    if (!mappingResult.IsSuccess)
                    {
                        logger.SubFlowOutputMappingFailed(
                            new Exception(mappingResult.Error.Message),
                            parentInstance.Id);
                    }

                    await instanceRepository.UpdateAsync(parentInstance, true, cancellationToken);
                    await uow.CommitAsync(cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(actionResult?.TransitionKey))
                {
                    await ExecuteErrorBoundaryTransitionAsync(
                        parentInstance,
                        parentWorkflow!,
                        actionResult.TransitionKey,
                        input.SubInstanceId,
                        lockKey,
                        input.Sync,
                        cancellationToken);
                }
                else if (actionResult?.ShouldContinue == true)
                {
                    await ResumePipelineAsync(
                        parentInstance,
                        parentWorkflow!,
                        input.SubInstanceId,
                        lockKey,
                        input.Sync,
                        cancellationToken);
                }

                logger.SubFlowFaultPropagatedToParent(input.SubInstanceId, input.InstanceId);
                SubFlowActivityHelper.SetSuccess(activity);
            }
            catch (Exception ex)
            {
                SubFlowActivityHelper.SetError(activity, ex.Message, ex);
                logger.SubFlowFaultProcessingFailed(ex, input.SubInstanceId, input.InstanceId);
                throw;
            }
        }
    }

    private static ExecutionError BuildExecutionError(SubFlowFaultedInput input)
    {
        var errorLayer = Enum.TryParse<ErrorLayer>(input.IncidentErrorLayer, ignoreCase: true, out var parsedLayer)
            ? parsedLayer
            : ErrorLayer.Task;

        var code = input.IncidentErrorCode ?? $"SubFlow:Faulted:{input.SubFlowName ?? input.Flow}";
        var message = input.IncidentMessage ?? "SubFlow faulted.";

        return new ExecutionError
        {
            TaskKey = input.IncidentTaskKey ?? input.SubFlowName ?? input.Flow,
            TaskType = "SubFlow",
            StatusCode = input.IncidentStatusCode,
            ErrorMessage = message,
            NormalizedError = new NormalizedError
            {
                Code = code,
                Layer = errorLayer,
                StatusCode = input.IncidentStatusCode,
                Message = message,
                Source = ErrorSource.ResultFailure,
                OriginalCode = input.IncidentErrorCode,
                IsTransient = false
            },
            ExecutionDurationMs = 0,
            Metadata = new Dictionary<string, object>
            {
                ["SubFlowName"] = input.SubFlowName ?? input.Flow,
                ["SubFlowInstanceId"] = input.SubInstanceId,
                ["FaultedState"] = input.FaultedState
            }
        };
    }

    private static InstanceIncident RecordIncident(
        Instance parentInstance,
        SubFlowFaultedInput input,
        ErrorAction boundaryAction,
        ErrorBoundaryLevel? boundaryLevel)
    {
        var incident = InstanceIncidentFactory.Create(
            state: parentInstance.GetCurrentState,
            transition: input.IncidentTransition ?? string.Empty,
            taskKey: input.IncidentTaskKey,
            message: $"SubFlow '{input.SubFlowName ?? input.Flow}' faulted: {input.IncidentMessage ?? "Unknown error"}",
            errorCode: $"SubFlow:Faulted:{input.IncidentErrorCode ?? "Unknown"}",
            errorLayer: "SubFlow",
            statusCode: input.IncidentStatusCode,
            stackTrace: input.IncidentStackTrace,
            boundaryAction: boundaryAction.ToString(),
            boundaryLevel: boundaryLevel?.ToString(),
            traceId: input.IncidentTraceId);

        parentInstance.AddIncident(incident);
        return incident;
    }

    private async Task ExecuteErrorBoundaryTransitionAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string transitionKey,
        Guid subInstanceId,
        string parentLockKey,
        bool sync,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = CreateWorkflowExecutionContext(
                parentInstance,
                parentWorkflow,
                transitionKey,
                isErrorBoundaryTransition: true,
                sync);

            var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);
            if (!result.IsSuccess)
            {
                throw new SubflowCompletionException(
                    parentWorkflow.Domain,
                    parentWorkflow.Key,
                    parentInstance.Id.ToString(),
                    result.Error.Code,
                    result.Error.Message ?? "Unknown error");
            }
        }
        catch
        {
            await RevertCorrelationInNewUowAsync(
                parentLockKey,
                subInstanceId,
                parentInstance.Id);
            throw;
        }
    }

    private async Task ResumePipelineAsync(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        Guid subInstanceId,
        string parentLockKey,
        bool sync,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = CreateWorkflowExecutionContext(
                parentInstance,
                parentWorkflow,
                transitionKey: string.Empty,
                isErrorBoundaryTransition: false,
                sync);
            input.Mode = ExecMode.Resume;
            input.Execution!.ResumeFrom = LifecycleOrder.ClearBusyOnResumeStep;
            input.Execution.IsSubFlowResume = true;
            input.Execution.SubFlowResumeInstanceId = subInstanceId;

            var result = await workflowExecutionService.ExecuteTransitionAsync(input, cancellationToken);
            if (!result.IsSuccess && result.Error.Code != WorkflowErrorCodes.AutoTransitionConditionNotMet)
            {
                throw new SubflowCompletionException(
                    parentWorkflow.Domain,
                    parentWorkflow.Key,
                    parentInstance.Id.ToString(),
                    result.Error.Code,
                    result.Error.Message ?? "Unknown error");
            }
        }
        catch
        {
            await RevertCorrelationInNewUowAsync(
                parentLockKey,
                subInstanceId,
                parentInstance.Id);
            throw;
        }
    }

    private static WorkflowExecutionContext CreateWorkflowExecutionContext(
        Instance parentInstance,
        Definitions.Workflow parentWorkflow,
        string transitionKey,
        bool isErrorBoundaryTransition,
        bool sync)
    {
        return new WorkflowExecutionContext
        {
            Domain = parentWorkflow.Domain,
            WorkflowKey = parentWorkflow.Key,
            WorkflowVersion = parentWorkflow.Version,
            InstanceId = parentInstance.Id.ToString(),
            TransitionKey = transitionKey,
            TriggerType = TriggerType.Automatic,
            Mode = ExecMode.Sync,
            // Preserve the faulting chain's caller mode so the parent's error-boundary
            // resume keeps starting/forwarding subflows synchronously when the caller was sync=true.
            CallerMode = sync ? ExecMode.Sync : ExecMode.Async,
            Headers = new Dictionary<string, string?>(),
            Actor = ExecutionActor.System,
            RequestedAt = DateTimeOffset.UtcNow,
            Execution = new ExecutionInfo
            {
                ExecutionChainId = Guid.NewGuid().ToString("N"),
                ChainDepth = 0
            },
            IsReentry = isErrorBoundaryTransition,
            IsErrorBoundaryTransition = isErrorBoundaryTransition
        };
    }

    private async Task RevertCorrelationInNewUowAsync(
        string parentLockKey,
        Guid subInstanceId,
        Guid parentInstanceId)
    {
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

            // S9 isolation rule: reload with ALL correlations (completed included) so the
            // revert operates on a tracked entity and cannot silently no-op.
            var tracked = await instanceRepository.FindWithAllCorrelationsAsync(parentInstanceId, cancellationToken)
                          ?? throw new InvalidOperationException(
                              $"Parent instance {parentInstanceId} was not found while reverting fault.");

            if (tracked.IsCompleted)
            {
                await revertUow.CommitAsync(cancellationToken);
                return;
            }

            var correlation = tracked.RevertCorrelation(subInstanceId);
            if (correlation == null)
            {
                logger.SubFlowCorrelationRevertTargetMissing(subInstanceId, parentInstanceId);
            }
            else
            {
                logger.SubFlowCorrelationReverted(subInstanceId, parentInstanceId);
                await instanceRepository.UpdateAsync(tracked, true, cancellationToken);
            }

            await revertUow.CommitAsync(cancellationToken);
        }
        catch (Exception revertEx)
        {
            logger.SubItemCorrelationRevertFailed(revertEx, parentInstanceId, subInstanceId);
        }
    }
}
