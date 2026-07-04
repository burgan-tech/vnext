using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Logging;

/// <summary>
/// Source-generated high-performance logging methods for workflow operations.
/// Uses LoggerMessage source generator for zero-allocation logging.
/// </summary>
public static partial class WorkflowLogs
{
    #region Transition Execution

    /// <summary>
    /// Logs when a state change occurs.
    /// </summary>
    [LoggerMessage(
        EventId = 10003,
        Level = LogLevel.Information,
        Message = "State changed from {FromState} to {ToState} for instance {InstanceId}")]
    public static partial void StateChanged(
        this ILogger logger,
        string fromState,
        string toState,
        Guid instanceId);

    /// <summary>
    /// Logs when a transition is successfully enqueued.
    /// </summary>
    [LoggerMessage(
        EventId = 10004,
        Level = LogLevel.Information,
        Message = "Successfully enqueued transition {TransitionKey} for instance {InstanceId} with job {JobName}")]
    public static partial void TransitionEnqueued(
        this ILogger logger,
        string transitionKey,
        string instanceId,
        string jobName);

    /// <summary>
    /// Logs when an instance is set to Busy before an async transition job is enqueued.
    /// </summary>
    [LoggerMessage(
        EventId = 10094,
        Level = LogLevel.Debug,
        Message = "Instance {InstanceId} set to Busy before async transition {TransitionKey} is processed")]
    public static partial void InstanceSetBusyForAsyncTransition(
        this ILogger logger,
        Guid instanceId,
        string transitionKey);

    /// <summary>
    /// Logs when recursive SubFlow busy propagation fails during async transition enqueue.
    /// </summary>
    [LoggerMessage(
        EventId = 10096,
        Level = LogLevel.Warning,
        Message =
            "SubFlow busy propagation failed for parent instance {ParentInstanceId} targeting subflow {SubFlowInstanceId}: {Reason}")]
    public static partial void SubFlowBusyPropagationFailedForAsyncTransition(
        this ILogger logger,
        Guid parentInstanceId,
        Guid subFlowInstanceId,
        string reason);

    /// <summary>
    /// Logs when a transition continuation event is received by the Inbox handler.
    /// </summary>
    [LoggerMessage(
        EventId = 10120,
        Level = LogLevel.Information,
        Message = "Transition continuation received for instance {InstanceId} transition {TransitionKey} (job {JobName})")]
    public static partial void TransitionContinuationReceived(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string jobName);

    /// <summary>
    /// Logs when a transition continuation event is ignored due to a domain mismatch.
    /// </summary>
    [LoggerMessage(
        EventId = 10121,
        Level = LogLevel.Debug,
        Message = "Transition continuation ignored: event domain {EventDomain} does not match runtime domain {RuntimeDomain} (instance {InstanceId})")]
    public static partial void TransitionContinuationIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid instanceId);

    /// <summary>
    /// Logs when a transition continuation has been enqueued as a Dapr job by the Inbox handler.
    /// </summary>
    [LoggerMessage(
        EventId = 10122,
        Level = LogLevel.Information,
        Message = "Transition continuation enqueued for instance {InstanceId} transition {TransitionKey} (job {JobName})")]
    public static partial void TransitionContinuationEnqueued(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string jobName);

    /// <summary>
    /// Logs when enqueuing a transition continuation job fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10123,
        Level = LogLevel.Error,
        Message = "Transition continuation enqueue failed for instance {InstanceId} transition {TransitionKey} (job {JobName}): {Reason}")]
    public static partial void TransitionContinuationEnqueueFailed(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string jobName,
        string reason);

    /// <summary>
    /// Logs when the direct Dapr enqueue of a chained continuation fails and the strategy
    /// falls back to publishing the continuation through the transactional outbox.
    /// </summary>
    [LoggerMessage(
        EventId = 10127,
        Level = LogLevel.Warning,
        Message = "Direct Dapr enqueue failed for instance {InstanceId} transition {TransitionKey} (job {JobName}); falling back to outbox: {Reason}")]
    public static partial void TransitionContinuationFellBackToOutbox(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string jobName,
        string reason);

    /// <summary>
    /// Logs when an instance cannot be found during busy marking — operation is skipped silently.
    /// </summary>
    [LoggerMessage(
        EventId = 10128,
        Level = LogLevel.Warning,
        Message = "Instance {InstanceId} not found for busy marker — skipping")]
    public static partial void InstanceNotFoundForBusyMarker(this ILogger logger, Guid instanceId);

    /// <summary>
    /// Logs when an instance is successfully marked Busy in an isolated RequiresNew UoW.
    /// </summary>
    [LoggerMessage(
        EventId = 10129,
        Level = LogLevel.Debug,
        Message = "Instance {InstanceId} marked Busy via isolated UoW")]
    public static partial void InstanceMarkedBusy(this ILogger logger, Guid instanceId);

    /// <summary>
    /// Logs when a foreign transition is rejected by the chain-token gate because the instance
    /// is Busy with an active auto-chain owned by a different token.
    /// </summary>
    [LoggerMessage(
        EventId = 10124,
        Level = LogLevel.Warning,
        Message = "Foreign transition {TransitionKey} rejected: instance {InstanceId} is Busy with an active chain (token mismatch)")]
    public static partial void ForeignChainTransitionRejected(
        this ILogger logger,
        string transitionKey,
        Guid instanceId);

    /// <summary>
    /// Logs when the chain reaper faults a stuck-Busy instance whose chain has no live job.
    /// </summary>
    [LoggerMessage(
        EventId = 10125,
        Level = LogLevel.Warning,
        Message = "Chain reaper faulted stuck instance {InstanceId} (chain {ChainToken}); heartbeat stale since {HeartbeatAt:o}")]
    public static partial void ChainReaperFaultedInstance(
        this ILogger logger,
        Guid instanceId,
        Guid? chainToken,
        DateTime? heartbeatAt);

    /// <summary>
    /// Logs the result of a chain reaper sweep.
    /// </summary>
    [LoggerMessage(
        EventId = 10126,
        Level = LogLevel.Information,
        Message = "Chain reaper sweep completed: {Faulted} faulted, {SkippedActive} skipped (active job)")]
    public static partial void ChainReaperSweepCompleted(
        this ILogger logger,
        int faulted,
        int skippedActive);

    /// <summary>
    /// Logs when the per-flow sweep timeout elapses before the reaper finishes a schema.
    /// </summary>
    [LoggerMessage(
        EventId = 10127,
        Level = LogLevel.Warning,
        Message = "Chain reaper sweep timed out for flow schema {FlowKey}; schema skipped this cycle")]
    public static partial void ChainReaperFlowSweepTimedOut(
        this ILogger logger,
        string flowKey);

    /// <summary>
    /// Logs when an active job already exists for the same instance and transition key,
    /// causing the request to be rejected with 409 Conflict.
    /// </summary>
    [LoggerMessage(
        EventId = 10095,
        Level = LogLevel.Warning,
        Message = "Transition job {JobName} already active for instance {InstanceId} transition {TransitionKey}, returning 409 conflict")]
    public static partial void TransitionJobAlreadyQueued(
        this ILogger logger,
        string jobName,
        Guid instanceId,
        string transitionKey);

    /// <summary>
    /// Logs when a cancel transition is detected.
    /// </summary>
    [LoggerMessage(
        EventId = 10005,
        Level = LogLevel.Information,
        Message = "Cancel transition detected for instance {InstanceId}")]
    public static partial void CancelTransitionDetected(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when skipping to finish step for cancel transition.
    /// </summary>
    [LoggerMessage(
        EventId = 10006,
        Level = LogLevel.Information,
        Message = "Skipping normal pipeline steps for cancel transition, jumping to Finish step for instance {InstanceId}")]
    public static partial void CancelSkipToFinish(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when an instance is being canceled.
    /// </summary>
    [LoggerMessage(
        EventId = 10007,
        Level = LogLevel.Information,
        Message = "Canceling instance {InstanceId}")]
    public static partial void InstanceCanceling(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when an instance is being completed.
    /// </summary>
    [LoggerMessage(
        EventId = 10008,
        Level = LogLevel.Information,
        Message = "Completing instance {InstanceId}")]
    public static partial void InstanceCompleting(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when an automatic transition is selected for execution.
    /// </summary>
    [LoggerMessage(
        EventId = 10009,
        Level = LogLevel.Information,
        Message = "Automatic transition selected for execution. TransitionKey={TransitionKey}, StateKey={StateKey}, InstanceId={InstanceId}")]
    public static partial void AutoTransitionSelected(
        this ILogger logger,
        string transitionKey,
        string stateKey,
        Guid instanceId);

    /// <summary>
    /// Logs when a transition timer is skipped due to missing configuration.
    /// </summary>
    [LoggerMessage(
        EventId = 10010,
        Level = LogLevel.Warning,
        Message = "Transition {TransitionKey} has no timer defined, skipping scheduling")]
    public static partial void TransitionTimerSkipped(
        this ILogger logger,
        string transitionKey);

    /// <summary>
    /// Logs when an updateData transition is detected.
    /// </summary>
    [LoggerMessage(
        EventId = 10011,
        Level = LogLevel.Information,
        Message = "UpdateData transition detected for instance {InstanceId}")]
    public static partial void UpdateDataTransitionDetected(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when skipping to finish step for updateData transition.
    /// </summary>
    [LoggerMessage(
        EventId = 10012,
        Level = LogLevel.Information,
        Message = "Skipping normal pipeline steps for updateData transition, jumping to Finalize step for instance {InstanceId}")]
    public static partial void UpdateDataSkipToFinish(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when an exit transition is detected.
    /// </summary>
    [LoggerMessage(
        EventId = 10013,
        Level = LogLevel.Information,
        Message = "Exit transition detected for instance {InstanceId}")]
    public static partial void ExitTransitionDetected(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when skipping to finish step for exit transition.
    /// </summary>
    [LoggerMessage(
        EventId = 10014,
        Level = LogLevel.Information,
        Message = "Skipping normal pipeline steps for exit transition, jumping to Finish step for instance {InstanceId}")]
    public static partial void ExitSkipToFinish(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when a transition rule validation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10040,
        Level = LogLevel.Warning,
        Message = "Transition rule failed for {TransitionKey} on instance {InstanceId}: {Reason}")]
    public static partial void TransitionRuleFailed(
        this ILogger logger,
        string transitionKey,
        Guid instanceId,
        string reason);

    /// <summary>
    /// Logs when automatic transition has no rule defined.
    /// </summary>
    [LoggerMessage(
        EventId = 10044,
        Level = LogLevel.Warning,
        Message = "Auto-transition {TransitionKey} has no rule defined")]
    public static partial void AutoTransitionNoRule(
        this ILogger logger,
        string transitionKey);

    /// <summary>
    /// Logs when maximum automatic transition hops are exceeded.
    /// </summary>
    [LoggerMessage(
        EventId = 10045,
        Level = LogLevel.Warning,
        Message = "Maximum auto transition hops ({MaxHops}) exceeded for instance {InstanceId}, chain {ExecutionChainId}")]
    public static partial void MaxAutoHopsExceeded(
        this ILogger logger,
        int maxHops,
        Guid instanceId,
        string? executionChainId);

    /// <summary>
    /// Logs when transition chain depth limit is exceeded in the pipeline.
    /// This indicates a potential infinite loop in automatic transition chains.
    /// </summary>
    [LoggerMessage(
        EventId = 10053,
        Level = LogLevel.Warning,
        Message = "Transition chain depth limit exceeded ({CurrentDepth}/{MaxDepth}) for transition '{TransitionKey}'")]
    public static partial void TransitionChainDepthExceeded(
        this ILogger logger,
        int currentDepth,
        int maxDepth,
        string? transitionKey);

    /// <summary>
    /// Logs when asynchronous transition enqueue fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10046,
        Level = LogLevel.Error,
        Message = "Asynchronous transition execution failed for {TransitionKey} on instance {InstanceId}")]
    public static partial void TransitionEnqueueFailed(
        this ILogger logger,
        string transitionKey,
        string instanceId);

    /// <summary>
    /// Logs when attempting to cancel an already completed instance.
    /// </summary>
    [LoggerMessage(
        EventId = 10047,
        Level = LogLevel.Warning,
        Message = "Cannot cancel instance {InstanceId}: already in {Status} state")]
    public static partial void CancelInstanceAlreadyCompleted(
        this ILogger logger,
        Guid instanceId,
        string status);

    /// <summary>
    /// Logs when target state is null during finish processing.
    /// </summary>
    [LoggerMessage(
        EventId = 10048,
        Level = LogLevel.Warning,
        Message = "Target state is null for instance {InstanceId}")]
    public static partial void TargetStateNull(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when no automatic transition condition is satisfied.
    /// </summary>
    [LoggerMessage(
        EventId = 10049,
        Level = LogLevel.Warning,
        Message = "No automatic transition condition is satisfied for current state. StateKey={StateKey}, InstanceId={InstanceId}, EvaluatedTransitions={TransitionKeys}")]
    public static partial void AutoTransitionConditionNotSatisfied(
        this ILogger logger,
        string stateKey,
        Guid instanceId,
        string transitionKeys);

    /// <summary>
    /// Logs when a Dynamic Expresso condition script cannot be decoded.
    /// </summary>
    [LoggerMessage(
        EventId = 10076,
        Level = LogLevel.Warning,
        Message = "Dynamic Expresso condition script has invalid encoding: {Reason}")]
    public static partial void DynamicExpressoConditionInvalidEncoding(
        this ILogger logger,
        string reason);

    /// <summary>
    /// Logs when Dynamic Expresso condition evaluation throws.
    /// </summary>
    [LoggerMessage(
        EventId = 10077,
        Level = LogLevel.Error,
        Message = "Dynamic Expresso condition evaluation failed: {Reason}")]
    public static partial void DynamicExpressoConditionEvaluationFailed(
        this ILogger logger,
        string reason);

    /// <summary>
    /// Logs when attempting to update data on an already completed instance.
    /// </summary>
    [LoggerMessage(
        EventId = 10054,
        Level = LogLevel.Warning,
        Message = "Cannot update data for instance {InstanceId}: already in {Status} state")]
    public static partial void UpdateDataInstanceAlreadyCompleted(
        this ILogger logger,
        Guid instanceId,
        string status);

    /// <summary>
    /// Logs when attempting to exit an already completed instance.
    /// </summary>
    [LoggerMessage(
        EventId = 10055,
        Level = LogLevel.Warning,
        Message = "Cannot exit instance {InstanceId}: already in {Status} state")]
    public static partial void ExitInstanceAlreadyCompleted(
        this ILogger logger,
        Guid instanceId,
        string status);

    /// <summary>
    /// Logs when inline transition execution fails during re-entry.
    /// </summary>
    [LoggerMessage(
        EventId = 10050,
        Level = LogLevel.Error,
        Message = "Inline execution failed for instance {InstanceId}, chain {ExecutionChainId}, workflow {WorkflowKey}, transition {TransitionKey}, trigger {TriggerType}: {ErrorMessage}")]
    public static partial void InlineExecutionFailed(
        this ILogger logger,
        string errorMessage,
        Guid instanceId,
        string? executionChainId,
        string workflowKey,
        string transitionKey,
        string triggerType);

    /// <summary>
    /// Logs when inline execution throws an exception during post-commit auto chain processing.
    /// </summary>
    [LoggerMessage(
        EventId = 10051,
        Level = LogLevel.Error,
        Message = "Inline execution exception for instance {InstanceId}, chain {ExecutionChainId}, transition {TransitionKey}")]
    public static partial void InlineExecutionException(
        this ILogger logger,
        Exception exception,
        Guid instanceId,
        string? executionChainId,
        string transitionKey);

    /// <summary>
    /// Logs when auto chain processing fails without any successful transitions.
    /// </summary>
    [LoggerMessage(
        EventId = 10052,
        Level = LogLevel.Warning,
        Message = "Auto chain processing failed: attempted {AttemptedCount} transitions with {Hops} hops, none succeeded")]
    public static partial void AutoChainProcessingFailed(
        this ILogger logger,
        int attemptedCount,
        int hops);

    /// <summary>
    /// Logs when scheduled jobs are being canceled for a state's transitions.
    /// </summary>
    [LoggerMessage(
        EventId = 10056,
        Level = LogLevel.Debug,
        Message = "Canceling scheduled jobs for instance {InstanceId}, state {StateKey}, transitions: {TransitionKeys}")]
    public static partial void ScheduledJobsCanceling(
        this ILogger logger,
        Guid instanceId,
        string stateKey,
        string transitionKeys);

    /// <summary>
    /// Logs when scheduled jobs cancellation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10057,
        Level = LogLevel.Warning,
        Message = "Failed to cancel scheduled jobs for instance {InstanceId}, transitions: {TransitionKeys}")]
    public static partial void ScheduledJobsCancellationFailed(
        this ILogger logger,
        Guid instanceId,
        string transitionKeys);

    /// <summary>
    /// Logs when state-specific scheduled jobs are successfully canceled.
    /// </summary>
    [LoggerMessage(
        EventId = 10058,
        Level = LogLevel.Information,
        Message = "Canceled {Count} scheduled jobs for instance {InstanceId}, transitions: {TransitionKeys}")]
    public static partial void StateTransitionsJobsCanceled(
        this ILogger logger,
        int count,
        Guid instanceId,
        string transitionKeys);

    /// <summary>
    /// Logs when error boundary Abort action is executed; instance will be marked Faulted.
    /// </summary>
    [LoggerMessage(
        EventId = 10059,
        Level = LogLevel.Information,
        Message = "Abort for task {TaskKey}. Instance will be marked Faulted.")]
    public static partial void ErrorBoundaryAbortInstanceFaulted(
        this ILogger logger,
        string taskKey);

    /// <summary>
    /// Logs when the transition pipeline begins execution with an execution profile applied.
    /// </summary>
    [LoggerMessage(
        EventId = 10060,
        Level = LogLevel.Debug,
        Message = "Pipeline executing with profile '{ProfileName}', {StepCount} steps, chain depth {ChainDepth}")]
    public static partial void PipelineExecutingWithProfile(
        this ILogger logger,
        string profileName,
        int stepCount,
        int chainDepth);

    /// <summary>
    /// Logs how many lifecycle steps were excluded for a transition according to the active profile.
    /// </summary>
    [LoggerMessage(
        EventId = 10061,
        Level = LogLevel.Debug,
        Message = "Profile '{ProfileName}' excluded {ExcludedCount} steps for transition {TransitionKey}")]
    public static partial void ProfileExcludedSteps(
        this ILogger logger,
        string profileName,
        int excludedCount,
        string transitionKey);

    /// <summary>
    /// Logs when an instance is about to be marked faulted due to an unhandled pipeline error.
    /// </summary>
    [LoggerMessage(
        EventId = 10130,
        Level = LogLevel.Warning,
        Message = "Marking instance {InstanceId} as faulted due to unhandled pipeline error: {ErrorCode} - {ErrorMessage}")]
    public static partial void InstanceFaultedDueToPipelineError(
        this ILogger logger,
        Guid instanceId,
        string? errorCode,
        string? errorMessage);

    /// <summary>
    /// Logs when an instance has been successfully persisted as faulted.
    /// </summary>
    [LoggerMessage(
        EventId = 10131,
        Level = LogLevel.Information,
        Message = "Instance {InstanceId} marked as faulted successfully. Client will receive Status = 'F'")]
    public static partial void InstanceFaultedSuccessfully(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when the workflow-level output script fails during sync response enrichment.
    /// Execution continues and falls back to raw instance attributes.
    /// </summary>
    [LoggerMessage(
        EventId = 10132,
        Level = LogLevel.Error,
        Message = "Workflow '{WorkflowKey}' output script failed. Falling back to raw instance attributes.")]
    public static partial void WorkflowOutputScriptFailed(
        this ILogger logger,
        string workflowKey,
        Exception exception);

    #endregion

    #region Task Execution

    /// <summary>
    /// Logs when a task execution fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10071,
        Level = LogLevel.Error,
        Message = "Task {TaskKey} ({TaskType}) failed for instance {InstanceId}")]
    public static partial void TaskExecutionFailed(
        this ILogger logger,
        Exception exception,
        string taskKey,
        string taskType,
        Guid instanceId);

    /// <summary>
    /// Logs when task input mapping/handler fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10072,
        Level = LogLevel.Error,
        Message = "Task input handler failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskInputHandlerFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when task output mapping/handler fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10073,
        Level = LogLevel.Error,
        Message = "Task output handler failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskOutputHandlerFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when task envelope creation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10075,
        Level = LogLevel.Error,
        Message = "Task envelope creation failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskEnvelopeCreationFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when task invocation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10076,
        Level = LogLevel.Error,
        Message = "Task invocation failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskInvocationFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when task instance resolution fails (for DirectTrigger, GetInstanceData).
    /// </summary>
    [LoggerMessage(
        EventId = 10077,
        Level = LogLevel.Error,
        Message = "Task instance resolution failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskInstanceResolutionFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when local task execution fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10078,
        Level = LogLevel.Error,
        Message = "Local task execution failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskLocalExecutionFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        string instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when remote task execution fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10079,
        Level = LogLevel.Error,
        Message = "Remote task execution failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskRemoteExecutionFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when task script compilation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10080,
        Level = LogLevel.Error,
        Message = "Task script compilation failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskScriptCompilationFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when task correlation creation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10081,
        Level = LogLevel.Error,
        Message = "Task correlation creation failed. TaskKey={TaskKey}, TaskType={TaskType}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void TaskCorrelationFailed(
        this ILogger logger,
        string taskKey,
        string taskType,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when notification default script retrieval fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10082,
        Level = LogLevel.Warning,
        Message = "Notification default script retrieval failed. TaskKey={TaskKey}, InstanceId={InstanceId}")]
    public static partial void NotificationScriptRetrievalFailed(
        this ILogger logger,
        string taskKey,
        Guid instanceId);

    #endregion

    #region SubFlow

    /// <summary>
    /// Logs when SubFlow configuration is missing or invalid.
    /// </summary>
    [LoggerMessage(
        EventId = 10074,
        Level = LogLevel.Error,
        Message = "SubFlow configuration invalid for state {StateName} on instance {InstanceId}")]
    public static partial void SubFlowConfigInvalid(
        this ILogger logger,
        string stateName,
        Guid instanceId);

    /// <summary>
    /// Logs when a SubFlow completion event is received.
    /// </summary>
    [LoggerMessage(
        EventId = 40011,
        Level = LogLevel.Information,
        Message = "SubFlow completion event received for SubInstance {SubInstanceId}, Parent {ParentInstanceId} in {Domain}/{Flow}")]
    public static partial void SubFlowEventReceived(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId,
        string domain,
        string flow);

    /// <summary>
    /// Logs when an event is silently ignored because it belongs to a different domain.
    /// </summary>
    [LoggerMessage(
        EventId = 40012,
        Level = LogLevel.Debug,
        Message = "Event silently ignored: event domain {EventDomain} does not match current runtime domain {RuntimeDomain}. SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowEventIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a correlation is not found for a completed SubFlow.
    /// </summary>
    [LoggerMessage(
        EventId = 40043,
        Level = LogLevel.Warning,
        Message = "Correlation not found for SubFlow instance {SubInstanceId}")]
    public static partial void SubFlowCorrelationNotFound(
        this ILogger logger,
        Guid subInstanceId);

    /// <summary>
    /// Logs when a correlation is marked as completed.
    /// </summary>
    [LoggerMessage(
        EventId = 40022,
        Level = LogLevel.Information,
        Message = "SubFlow correlation completed for SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowCorrelationCompleted(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a SubFlow correlation is reverted due to pipeline failure.
    /// </summary>
    [LoggerMessage(
        EventId = 40023,
        Level = LogLevel.Warning,
        Message = "SubFlow correlation reverted for SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowCorrelationReverted(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a correlation revert finds no matching completed correlation —
    /// the parent may be permanently stuck Busy and requires manual intervention.
    /// </summary>
    [LoggerMessage(
        EventId = 40119,
        Level = LogLevel.Error,
        Message = "SubFlow correlation revert found no completed correlation for SubInstance {SubInstanceId}, Parent {ParentInstanceId} — parent may be stuck Busy")]
    public static partial void SubFlowCorrelationRevertTargetMissing(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a SubFlow state change event is received.
    /// </summary>
    [LoggerMessage(
        EventId = 40027,
        Level = LogLevel.Information,
        Message = "SubFlow state change received for SubInstance {SubInstanceId}, Parent {ParentInstanceId}, NewState: {NewState}")]
    public static partial void SubFlowStateChangeReceived(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId,
        string newState);

    /// <summary>
    /// Logs when a SubFlow state change is successfully applied to parent.
    /// </summary>
    [LoggerMessage(
        EventId = 40028,
        Level = LogLevel.Information,
        Message = "SubFlow state change applied for SubInstance {SubInstanceId}, Parent {ParentInstanceId}, NewState: {NewState}")]
    public static partial void SubFlowStateChangeApplied(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId,
        string newState);

    /// <summary>
    /// Logs when a SubFlow state changed event is received by the hook.
    /// </summary>
    [LoggerMessage(
        EventId = 40029,
        Level = LogLevel.Information,
        Message = "SubFlow state changed event received for SubInstance {SubInstanceId}, Parent {ParentInstanceId}, NewState: {NewState}")]
    public static partial void SubFlowStateChangedEventReceived(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId,
        string newState);

    /// <summary>
    /// Logs when SubFlow state update fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40079,
        Level = LogLevel.Error,
        Message = "SubFlow state update failed for SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowStateUpdateFailed(
        this ILogger logger,
        Exception exception,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when SubFlow state update fails with error details.
    /// </summary>
    [LoggerMessage(
        EventId = 40102,
        Level = LogLevel.Warning,
        Message = "SubFlow state update failed for SubInstance {SubInstanceId}, Parent {ParentInstanceId}. Error: [{ErrorCode}] {ErrorMessage}")]
    public static partial void SubFlowStateUpdateFailedWithError(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId,
        string errorCode,
        string errorMessage);

    /// <summary>
    /// Logs when parent workflow continuation starts after SubFlow completion.
    /// </summary>
    [LoggerMessage(
        EventId = 40013,
        Level = LogLevel.Information,
        Message = "Parent workflow continuation started for instance {ParentInstanceId} in state {CurrentState}")]
    public static partial void SubFlowParentContinuationStarted(
        this ILogger logger,
        Guid parentInstanceId,
        string currentState);

    /// <summary>
    /// Logs when SubFlow output mapping starts.
    /// </summary>
    [LoggerMessage(
        EventId = 40014,
        Level = LogLevel.Information,
        Message = "SubFlow output mapping started for parent instance {ParentInstanceId}")]
    public static partial void SubFlowOutputMappingStarted(
        this ILogger logger,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when SubFlow output mapping script execution fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40080,
        Level = LogLevel.Error,
        Message = "SubFlow output mapping failed for parent instance {ParentInstanceId}")]
    public static partial void SubFlowOutputMappingFailed(
        this ILogger logger,
        Exception exception,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when pipeline is resumed after SubFlow completion.
    /// </summary>
    [LoggerMessage(
        EventId = 40015,
        Level = LogLevel.Information,
        Message = "Resuming pipeline for parent instance {ParentInstanceId} after SubFlow completion")]
    public static partial void SubFlowPipelineResumed(
        this ILogger logger,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when SubFlow completion processing fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40073,
        Level = LogLevel.Error,
        Message = "SubFlow completion failed for SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowCompletionFailed(
        this ILogger logger,
        Exception exception,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a SubFlow start operation completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 40030,
        Level = LogLevel.Information,
        Message = "SubFlow {SubFlowKey} started successfully for parent instance {ParentInstanceId}")]
    public static partial void SubFlowStarted(
        this ILogger logger,
        string subFlowKey,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a SubFlow start operation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40080,
        Level = LogLevel.Error,
        Message = "SubFlow {SubFlowKey} start failed for parent instance {ParentInstanceId}: {ErrorCode} - {ErrorMessage}")]
    public static partial void SubFlowStartFailed(
        this ILogger logger,
        string subFlowKey,
        Guid parentInstanceId,
        string errorCode,
        string errorMessage);

    /// <summary>
    /// Logs when instance is not found during subflow start.
    /// </summary>
    [LoggerMessage(
        EventId = 40081,
        Level = LogLevel.Error,
        Message = "Instance {InstanceId} not found while starting subflow for correlation {CorrelationId}")]
    public static partial void SubFlowInstanceNotFound(
        this ILogger logger,
        Guid instanceId,
        Guid correlationId);

    /// <summary>
    /// Logs when correlation is not found during subflow start.
    /// </summary>
    [LoggerMessage(
        EventId = 40082,
        Level = LogLevel.Error,
        Message = "Correlation {CorrelationId} not found for instance {InstanceId}")]
    public static partial void SubFlowCorrelationNotFoundForStart(
        this ILogger logger,
        Guid correlationId,
        Guid instanceId);

    /// <summary>
    /// Logs when target state is not found or has no SubFlow configuration.
    /// </summary>
    [LoggerMessage(
        EventId = 40083,
        Level = LogLevel.Error,
        Message = "Target state {TargetStateKey} not found or has no SubFlow configuration for instance {InstanceId}")]
    public static partial void SubFlowTargetStateNotFound(
        this ILogger logger,
        string targetStateKey,
        Guid instanceId);

    /// <summary>
    /// Logs when SubFlow forward fails with a client error (validation, not found, etc.).
    /// Client errors are returned to the user without faulting the instance.
    /// </summary>
    [LoggerMessage(
        EventId = 40084,
        Level = LogLevel.Warning,
        Message = "SubFlow forward failed with client error for instance {InstanceId}: {ErrorCode} - {ErrorMessage}")]
    public static partial void SubFlowForwardClientError(
        this ILogger logger,
        Guid instanceId,
        string errorCode,
        string errorMessage);

    /// <summary>
    /// Logs when SubFlow forward fails with a system error (dependency, transient, etc.).
    /// System errors cause the instance to be marked as faulted.
    /// </summary>
    [LoggerMessage(
        EventId = 40085,
        Level = LogLevel.Error,
        Message = "SubFlow forward failed with system error for instance {InstanceId}: {ErrorCode} - {ErrorMessage}")]
    public static partial void SubFlowForwardSystemError(
        this ILogger logger,
        Guid instanceId,
        string errorCode,
        string errorMessage);

    /// <summary>
    /// Logs when forwarding a transition to a subflow instance is started.
    /// </summary>
    [LoggerMessage(
        EventId = 40086,
        Level = LogLevel.Debug,
        Message = "Forwarding transition {TransitionKey} to subflow instance {SubflowInstanceId} for parent instance {ParentInstanceId}")]
    public static partial void SubFlowForwardStarted(
        this ILogger logger,
        string transitionKey,
        Guid subflowInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a transition was successfully forwarded to a subflow instance.
    /// </summary>
    [LoggerMessage(
        EventId = 40087,
        Level = LogLevel.Information,
        Message = "Successfully forwarded transition {TransitionKey} to subflow instance {SubflowInstanceId} for parent instance {ParentInstanceId}")]
    public static partial void SubFlowForwardSucceeded(
        this ILogger logger,
        string transitionKey,
        Guid subflowInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when forwarding a transition to a subflow instance failed.
    /// </summary>
    [LoggerMessage(
        EventId = 40088,
        Level = LogLevel.Warning,
        Message = "Forward to subflow instance {SubflowInstanceId} failed for parent instance {ParentInstanceId}, transition {TransitionKey}: {ErrorCode} - {ErrorMessage}")]
    public static partial void SubFlowForwardFailed(
        this ILogger logger,
        Guid subflowInstanceId,
        Guid parentInstanceId,
        string transitionKey,
        string errorCode,
        string errorMessage);

    /// <summary>
    /// Logs when a SubFlow fault propagation event is received (upward: child faulted, notifying parent).
    /// </summary>
    [LoggerMessage(
        EventId = 40110,
        Level = LogLevel.Information,
        Message = "SubFlow fault event received for SubInstance {SubInstanceId}, Parent {ParentInstanceId} in {Domain}/{Flow}")]
    public static partial void SubFlowFaultReceived(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId,
        string domain,
        string flow);

    /// <summary>
    /// Logs when a SubFlow fault event is ignored due to domain mismatch.
    /// </summary>
    [LoggerMessage(
        EventId = 40111,
        Level = LogLevel.Debug,
        Message = "SubFlow fault event ignored: event domain {EventDomain} does not match runtime domain {RuntimeDomain}. SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowFaultIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a SubFlow fault is successfully propagated to the parent instance.
    /// </summary>
    [LoggerMessage(
        EventId = 40112,
        Level = LogLevel.Information,
        Message = "SubFlow fault propagated to parent: SubInstance {SubInstanceId} -> Parent {ParentInstanceId}")]
    public static partial void SubFlowFaultPropagatedToParent(
        this ILogger logger,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when SubFlow fault propagation processing fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40113,
        Level = LogLevel.Error,
        Message = "SubFlow fault propagation failed for SubInstance {SubInstanceId}, Parent {ParentInstanceId}")]
    public static partial void SubFlowFaultProcessingFailed(
        this ILogger logger,
        Exception exception,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a downward child SubFlow fault request is received (parent faulted, notifying child).
    /// </summary>
    [LoggerMessage(
        EventId = 40114,
        Level = LogLevel.Information,
        Message = "Child subflow fault request received for instance {InstanceId} in {Domain}/{Flow}")]
    public static partial void ChildSubflowFaultRequestReceived(
        this ILogger logger,
        Guid instanceId,
        string domain,
        string flow);

    /// <summary>
    /// Logs when a child subflow fault request is ignored due to domain mismatch.
    /// </summary>
    [LoggerMessage(
        EventId = 40115,
        Level = LogLevel.Debug,
        Message = "Child subflow fault request ignored: event domain {EventDomain} does not match runtime domain {RuntimeDomain}. Instance {InstanceId}, Flow {Flow}")]
    public static partial void ChildSubflowFaultIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when a child subflow is successfully faulted by downward propagation from parent.
    /// </summary>
    [LoggerMessage(
        EventId = 40116,
        Level = LogLevel.Information,
        Message = "Child subflow fault applied: Instance {InstanceId} faulted by parent {ParentInstanceId}")]
    public static partial void ChildSubflowFaultApplied(
        this ILogger logger,
        Guid instanceId,
        Guid parentInstanceId);

    #endregion

    #region Instance Management

    /// <summary>
    /// Logs when an instance is not found.
    /// </summary>
    [LoggerMessage(
        EventId = 20040,
        Level = LogLevel.Warning,
        Message = "Instance {InstanceId} not found for workflow {WorkflowKey}")]
    public static partial void InstanceNotFound(
        this ILogger logger,
        Guid instanceId,
        string workflowKey);

    /// <summary>
    /// Logs when failed to acquire distributed lock for instance.
    /// </summary>
    [LoggerMessage(
        EventId = 40044,
        Level = LogLevel.Warning,
        Message = "Failed to acquire lock for instance {InstanceId}")]
    public static partial void InstanceLockFailed(
        this ILogger logger,
        string instanceId);

    /// <summary>
    /// Logs when the chain lock lease could not be extended between chained transitions;
    /// the chain stops instead of continuing without a held lease.
    /// </summary>
    [LoggerMessage(
        EventId = 40052,
        Level = LogLevel.Warning,
        Message = "Failed to extend chain lock lease for instance {InstanceId} before chained transition {TransitionKey}; stopping chain")]
    public static partial void TransitionLockExtendFailed(
        this ILogger logger,
        string instanceId,
        string transitionKey);

    /// <summary>
    /// Logs when start transition validation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40045,
        Level = LogLevel.Warning,
        Message = "Start transition validation failed for instance {InstanceId}: {ErrorCode}")]
    public static partial void StartTransitionValidationFailed(
        this ILogger logger,
        Guid instanceId,
        string errorCode);

    /// <summary>
    /// Logs when a transition request fails pre-dispatch validation (schema or policy).
    /// Emitted by the AppService guard so both sync=true and sync=false callers see
    /// the same 400 Bad Request behaviour for invalid payloads.
    /// </summary>
    [LoggerMessage(
        EventId = 40051,
        Level = LogLevel.Warning,
        Message = "Transition validation failed for instance {InstanceId} on transition {TransitionKey}: {ErrorCode}")]
    public static partial void TransitionValidationFailed(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string errorCode);

    /// <summary>
    /// Logs when workflow timeout is scheduled.
    /// </summary>
    [LoggerMessage(
        EventId = 40016,
        Level = LogLevel.Information,
        Message = "Scheduled workflow timeout for instance {InstanceId} with duration {Duration}, executing at {TimeoutAt}")]
    public static partial void WorkflowTimeoutScheduled(
        this ILogger logger,
        Guid instanceId,
        string duration,
        DateTime timeoutAt);

    /// <summary>
    /// Logs when workflow timeout scheduling fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40074,
        Level = LogLevel.Error,
        Message = "Failed to schedule workflow timeout for instance {InstanceId}")]
    public static partial void WorkflowTimeoutSchedulingFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    /// <summary>
    /// Logs when timeout mapping script fails and static timer duration is used as fallback.
    /// </summary>
    [LoggerMessage(
        EventId = 40100,
        Level = LogLevel.Warning,
        Message = "Timeout mapping failed for instance {InstanceId}, falling back to static duration {Duration}. Error: {ErrorMessage}")]
    public static partial void TimeoutMappingFallback(
        this ILogger logger,
        Guid instanceId,
        string duration,
        string errorMessage);

    /// <summary>
    /// Logs when timeout mapping script executes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 40101,
        Level = LogLevel.Information,
        Message = "Timeout mapping resolved for instance {InstanceId}, schedule type: {ScheduleType}")]
    public static partial void TimeoutMappingResolved(
        this ILogger logger,
        Guid instanceId,
        string scheduleType);

    /// <summary>
    /// Logs when workflow definition is not found.
    /// </summary>
    [LoggerMessage(
        EventId = 40046,
        Level = LogLevel.Warning,
        Message = "Workflow {WorkflowKey} not found: {ErrorCode}")]
    public static partial void WorkflowNotFoundWarning(
        this ILogger logger,
        string workflowKey,
        string errorCode);

    /// <summary>
    /// Logs when timeout configuration is missing for workflow.
    /// </summary>
    [LoggerMessage(
        EventId = 40047,
        Level = LogLevel.Warning,
        Message = "Timeout configuration missing for workflow {WorkflowKey}")]
    public static partial void TimeoutConfigMissing(
        this ILogger logger,
        string workflowKey);

    #endregion

    #region Instance Cancellation

    /// <summary>
    /// Logs when an InstanceCanceledEvent is received.
    /// </summary>
    [LoggerMessage(
        EventId = 40018,
        Level = LogLevel.Information,
        Message = "InstanceCanceledEvent received for instance {InstanceId} in {Flow}")]
    public static partial void InstanceCanceledEventReceived(
        this ILogger logger,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when an InstanceCanceledEvent is silently ignored because it belongs to a different domain.
    /// </summary>
    [LoggerMessage(
        EventId = 40021,
        Level = LogLevel.Debug,
        Message = "InstanceCanceledEvent silently ignored: event domain {EventDomain} does not match current runtime domain {RuntimeDomain}. Instance {InstanceId}, Flow {Flow}")]
    public static partial void InstanceCanceledEventIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when instance jobs are processed during cancellation.
    /// </summary>
    [LoggerMessage(
        EventId = 40019,
        Level = LogLevel.Information,
        Message = "Instance cancellation jobs processed for instance {InstanceId}, {JobCount} jobs canceled")]
    public static partial void InstanceCanceledJobsProcessed(
        this ILogger logger,
        Guid instanceId,
        int jobCount);

    /// <summary>
    /// Logs when instance cancellation processing fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40077,
        Level = LogLevel.Error,
        Message = "Instance cancellation processing failed for instance {InstanceId}")]
    public static partial void InstanceCanceledProcessingFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    /// <summary>
    /// Logs when a single job deletion fails during instance cancellation.
    /// </summary>
    [LoggerMessage(
        EventId = 40103,
        Level = LogLevel.Error,
        Message = "Failed to delete job {JobId} during cancellation for instance {InstanceId}")]
    public static partial void InstanceJobDeletionFailed(
        this ILogger logger,
        Exception exception,
        Guid jobId,
        Guid instanceId);

    #endregion

    #region Instance Completion Cleanup

    /// <summary>
    /// Logs when an InstanceCompletedCleanupEvent is received.
    /// </summary>
    [LoggerMessage(
        EventId = 40090,
        Level = LogLevel.Information,
        Message = "InstanceCompletedCleanupEvent received for instance {InstanceId} in {Flow}")]
    public static partial void InstanceCompletedCleanupEventReceived(
        this ILogger logger,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when an InstanceCompletedCleanupEvent is silently ignored because it belongs to a different domain.
    /// </summary>
    [LoggerMessage(
        EventId = 40091,
        Level = LogLevel.Debug,
        Message = "InstanceCompletedCleanupEvent silently ignored: event domain {EventDomain} does not match current runtime domain {RuntimeDomain}. Instance {InstanceId}, Flow {Flow}")]
    public static partial void InstanceCompletedCleanupEventIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when instance completion cleanup processing succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 40092,
        Level = LogLevel.Information,
        Message = "Instance completion cleanup succeeded for instance {InstanceId}")]
    public static partial void InstanceCompletedCleanupSucceeded(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when instance completion cleanup processing fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40093,
        Level = LogLevel.Error,
        Message = "Instance completion cleanup processing failed for instance {InstanceId}")]
    public static partial void InstanceCompletedCleanupProcessingFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    /// <summary>
    /// Logs when processing cleanup for completed instance (event hook).
    /// </summary>
    [LoggerMessage(
        EventId = 40098,
        Level = LogLevel.Information,
        Message = "Processing cleanup for completed instance {InstanceId}, flow: {Flow}")]
    public static partial void InstanceCompletedCleanupHookProcessing(
        this ILogger logger,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when completed instance cleanup hook fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40099,
        Level = LogLevel.Error,
        Message = "Failed to process cleanup for completed instance {InstanceId}")]
    public static partial void InstanceCompletedCleanupHookFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    #endregion

    #region Instance Fault Cleanup

    /// <summary>
    /// Logs when an InstanceFaultedCleanupEvent is received.
    /// </summary>
    [LoggerMessage(
        EventId = 40094,
        Level = LogLevel.Information,
        Message = "InstanceFaultedCleanupEvent received for instance {InstanceId} in {Flow}")]
    public static partial void InstanceFaultedCleanupEventReceived(
        this ILogger logger,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when an InstanceFaultedCleanupEvent is silently ignored because it belongs to a different domain.
    /// </summary>
    [LoggerMessage(
        EventId = 40095,
        Level = LogLevel.Debug,
        Message = "InstanceFaultedCleanupEvent silently ignored: event domain {EventDomain} does not match current runtime domain {RuntimeDomain}. Instance {InstanceId}, Flow {Flow}")]
    public static partial void InstanceFaultedCleanupEventIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when instance fault cleanup processing succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 40096,
        Level = LogLevel.Information,
        Message = "Instance fault cleanup succeeded for instance {InstanceId}")]
    public static partial void InstanceFaultedCleanupSucceeded(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when instance fault cleanup processing fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40097,
        Level = LogLevel.Error,
        Message = "Instance fault cleanup processing failed for instance {InstanceId}")]
    public static partial void InstanceFaultedCleanupProcessingFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    /// <summary>
    /// Logs when processing cleanup for faulted instance (event hook).
    /// </summary>
    [LoggerMessage(
        EventId = 40100,
        Level = LogLevel.Information,
        Message = "Processing cleanup for faulted instance {InstanceId}, flow: {Flow}")]
    public static partial void InstanceFaultedCleanupHookProcessing(
        this ILogger logger,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when faulted instance cleanup hook fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40101,
        Level = LogLevel.Error,
        Message = "Failed to process cleanup for faulted instance {InstanceId}")]
    public static partial void InstanceFaultedCleanupHookFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    #endregion

    #region Child Subflow Cancellation

    /// <summary>
    /// Logs when a ChildSubflowCancelRequestedEvent is received.
    /// </summary>
    [LoggerMessage(
        EventId = 40020,
        Level = LogLevel.Information,
        Message = "ChildSubflowCancelRequestedEvent received for instance {InstanceId} in {Domain}/{Flow}")]
    public static partial void ChildSubflowCancelRequestReceived(
        this ILogger logger,
        Guid instanceId,
        string domain,
        string flow);

    /// <summary>
    /// Logs when a ChildSubflowCancelRequestedEvent is silently ignored because it belongs to a different domain.
    /// </summary>
    [LoggerMessage(
        EventId = 40030,
        Level = LogLevel.Debug,
        Message = "ChildSubflowCancelRequestedEvent silently ignored: event domain {EventDomain} does not match current runtime domain {RuntimeDomain}. Instance {InstanceId}, Flow {Flow}")]
    public static partial void ChildSubflowCancelEventIgnoredDomainMismatch(
        this ILogger logger,
        string eventDomain,
        string runtimeDomain,
        Guid instanceId,
        string flow);

    /// <summary>
    /// Logs when child subflow cancellation succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 40021,
        Level = LogLevel.Information,
        Message = "Child subflow cancellation succeeded for instance {InstanceId}")]
    public static partial void ChildSubflowCancelSucceeded(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when child subflow cancellation fails with a warning.
    /// </summary>
    [LoggerMessage(
        EventId = 40050,
        Level = LogLevel.Warning,
        Message = "Child subflow cancellation failed for instance {InstanceId}, transition was not successful")]
    public static partial void ChildSubflowCancelFailed(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when child subflow cancellation encounters an error.
    /// </summary>
    [LoggerMessage(
        EventId = 40078,
        Level = LogLevel.Error,
        Message = "Child subflow cancellation error for instance {InstanceId}")]
    public static partial void ChildSubflowCancelError(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    #endregion

    #region Background Jobs

    /// <summary>
    /// Logs when a background job completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 40017,
        Level = LogLevel.Information,
        Message = "Job {JobName} completed: {TransitionKey} for instance {InstanceId}")]
    public static partial void JobCompleted(
        this ILogger logger,
        string jobName,
        string transitionKey,
        Guid instanceId);

    /// <summary>
    /// Logs when a background job fails with an exception.
    /// </summary>
    [LoggerMessage(
        EventId = 40075,
        Level = LogLevel.Error,
        Message = "Job {JobName} failed for instance {InstanceId}")]
    public static partial void JobFailed(
        this ILogger logger,
        Exception exception,
        string jobName,
        Guid instanceId);

    /// <summary>
    /// Logs when a background job fails with an error message (used for Result pattern failures).
    /// </summary>
    [LoggerMessage(
        EventId = 40075,
        Level = LogLevel.Error,
        Message = "Job {JobName} failed for instance {InstanceId}: {ErrorMessage}")]
    public static partial void JobFailed(
        this ILogger logger,
        string jobName,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs when a job is cancelled.
    /// </summary>
    [LoggerMessage(
        EventId = 40048,
        Level = LogLevel.Warning,
        Message = "Job {JobName} cancelled: {TransitionKey} for instance {InstanceId}")]
    public static partial void JobCancelled(
        this ILogger logger,
        string jobName,
        string transitionKey,
        Guid instanceId);

    /// <summary>
    /// Logs when a job's execution budget is exceeded (timeout.layer=job).
    /// </summary>
    [LoggerMessage(
        EventId = 40117,
        Level = LogLevel.Error,
        Message = "Job {JobName} timed out after {TimeoutSeconds}s: {TransitionKey} for instance {InstanceId} [timeout.layer=job]")]
    public static partial void JobTimedOut(
        this ILogger logger,
        string jobName,
        int timeoutSeconds,
        string transitionKey,
        Guid instanceId);

    /// <summary>
    /// Logs when a job handler is cancelled by Dapr or an external signal (not host shutdown).
    /// </summary>
    [LoggerMessage(
        EventId = 40118,
        Level = LogLevel.Error,
        Message = "Job {JobName} cancelled by Dapr/external signal: {TransitionKey} for instance {InstanceId} [timeout.layer=dapr-cancel]")]
    public static partial void JobCancelledByExternal(
        this ILogger logger,
        string jobName,
        string transitionKey,
        Guid instanceId);

    #endregion

    #region Runtime

    /// <summary>
    /// Logs when workflow instance deserialization fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40076,
        Level = LogLevel.Error,
        Message = "Failed to deserialize workflow instance data for schema {Schema}, instance {InstanceKey}, version {Version}")]
    public static partial void InstanceDeserializationFailed(
        this ILogger logger,
        Exception exception,
        string schema,
        string? instanceKey,
        string version);

    #endregion

    #region Query Operations

    /// <summary>
    /// Logs when SubFlow transitions query fails.
    /// </summary>
    [LoggerMessage(
        EventId = 40049,
        Level = LogLevel.Warning,
        Message = "Failed to get transitions from SubFlow {SubFlowDomain}/{SubFlowName} for instance {InstanceId}")]
    public static partial void SubFlowTransitionsQueryFailed(
        this ILogger logger,
        Exception exception,
        string subFlowDomain,
        string subFlowName,
        Guid instanceId);

    #endregion

    #region Post-Commit Execution

    /// <summary>
    /// Logs when post-commit executor starts processing jobs.
    /// </summary>
    [LoggerMessage(
        EventId = 10090,
        Level = LogLevel.Debug,
        Message = "Post-commit executor starting for instance {InstanceId}, processing {JobCount} job(s)")]
    public static partial void PostCommitExecutorStarting(
        this ILogger logger,
        Guid instanceId,
        int jobCount);

    /// <summary>
    /// Logs when a post-commit job completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 10091,
        Level = LogLevel.Debug,
        Message = "Post-commit job {JobType} completed for instance {InstanceId}")]
    public static partial void PostCommitJobCompleted(
        this ILogger logger,
        Guid instanceId,
        string jobType);

    /// <summary>
    /// Logs when a post-commit job fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10092,
        Level = LogLevel.Error,
        Message = "Post-commit job {JobType} failed for instance {InstanceId}: {ErrorMessage}")]
    public static partial void PostCommitJobFailed(
        this ILogger logger,
        Guid instanceId,
        string jobType,
        string errorMessage);

    /// <summary>
    /// Logs when post-commit executor completes all jobs.
    /// </summary>
    [LoggerMessage(
        EventId = 10093,
        Level = LogLevel.Debug,
        Message = "Post-commit executor completed for instance {InstanceId}, processed {JobCount} job(s)")]
    public static partial void PostCommitExecutorCompleted(
        this ILogger logger,
        Guid instanceId,
        int jobCount);

    #endregion

    #region Resource Lock

    /// <summary>
    /// Logs when a distributed resource lock is successfully acquired.
    /// </summary>
    [LoggerMessage(
        EventId = 10100,
        Level = LogLevel.Information,
        Message = "Resource lock acquired: Key={ResourceKey}, Owner={Owner}, TTL={TtlSeconds}s")]
    public static partial void ResourceLockAcquired(
        this ILogger logger,
        string resourceKey,
        string owner,
        int ttlSeconds);

    /// <summary>
    /// Logs when a resource lock acquisition fails because the resource is already locked.
    /// </summary>
    [LoggerMessage(
        EventId = 10101,
        Level = LogLevel.Warning,
        Message = "Resource lock conflict: Key={ResourceKey}, Owner={Owner}. Resource is already locked")]
    public static partial void ResourceLockAcquireConflict(
        this ILogger logger,
        string resourceKey,
        string owner);

    /// <summary>
    /// Logs when a distributed resource lock is successfully released.
    /// </summary>
    [LoggerMessage(
        EventId = 10103,
        Level = LogLevel.Information,
        Message = "Resource lock released: Key={ResourceKey}, Owner={Owner}")]
    public static partial void ResourceLockReleased(
        this ILogger logger,
        string resourceKey,
        string owner);

    /// <summary>
    /// Logs when a resource lock release fails (lock not held by this owner).
    /// </summary>
    [LoggerMessage(
        EventId = 10104,
        Level = LogLevel.Warning,
        Message = "Resource lock release failed: Key={ResourceKey}, Owner={Owner}, Status={Status}")]
    public static partial void ResourceLockReleaseFailed(
        this ILogger logger,
        string resourceKey,
        string owner,
        string status);

    /// <summary>
    /// Logs when a distributed resource lock TTL is successfully extended.
    /// </summary>
    [LoggerMessage(
        EventId = 10106,
        Level = LogLevel.Information,
        Message = "Resource lock extended: Key={ResourceKey}, Owner={Owner}, TTL={TtlSeconds}s")]
    public static partial void ResourceLockExtended(
        this ILogger logger,
        string resourceKey,
        string owner,
        int ttlSeconds);

    /// <summary>
    /// Logs when a resource lock extension fails (lock not held by this owner).
    /// </summary>
    [LoggerMessage(
        EventId = 10107,
        Level = LogLevel.Warning,
        Message = "Resource lock extend failed: Key={ResourceKey}, Owner={Owner}")]
    public static partial void ResourceLockExtendFailed(
        this ILogger logger,
        string resourceKey,
        string owner);

    #endregion

    #region Instance Retry
 
    /// <summary>
    /// Logs when an instance retry is requested.
    /// </summary>
    [LoggerMessage(
        EventId = 20050,
        Level = LogLevel.Information,
        Message = "Instance retry requested for {InstanceId} in workflow {WorkflowKey}")]
    public static partial void InstanceRetryRequested(
        this ILogger logger,
        string instanceId,
        string workflowKey);
 
    /// <summary>
    /// Logs when an instance is successfully unfaulted.
    /// </summary>
    [LoggerMessage(
        EventId = 20051,
        Level = LogLevel.Information,
        Message = "Instance {InstanceId} unfaulted, ready for retry")]
    public static partial void InstanceUnfaulted(
        this ILogger logger,
        Guid instanceId);
 
    /// <summary>
    /// Logs when an instance retry succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 20052,
        Level = LogLevel.Information,
        Message = "Instance {InstanceId} retry succeeded")]
    public static partial void InstanceRetrySucceeded(
        this ILogger logger,
        Guid instanceId);
 
    /// <summary>
    /// Logs when an instance retry fails.
    /// </summary>
    [LoggerMessage(
        EventId = 20053,
        Level = LogLevel.Warning,
        Message = "Instance {InstanceId} retry failed: {Reason}")]
    public static partial void InstanceRetryFailed(
        this ILogger logger,
        Guid instanceId,
        string reason);
 
    #endregion

    #region Service Discovery

    /// <summary>
    /// Logs when bulk domain cache refresh starts.
    /// </summary>
    [LoggerMessage(
        EventId = 50001,
        Level = LogLevel.Information,
        Message = "Bulk domain cache refresh started")]
    public static partial void BulkCacheRefreshStarted(
        this ILogger logger);

    /// <summary>
    /// Logs when bulk domain cache refresh completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 50002,
        Level = LogLevel.Information,
        Message = "Bulk domain cache refreshed: {DomainCount} domains cached")]
    public static partial void BulkCacheRefreshed(
        this ILogger logger,
        int domainCount);

    /// <summary>
    /// Logs when bulk domain cache refresh fails.
    /// </summary>
    [LoggerMessage(
        EventId = 50003,
        Level = LogLevel.Warning,
        Message = "Bulk domain cache refresh failed: {Error}")]
    public static partial void BulkCacheRefreshFailed(
        this ILogger logger,
        string error);

    /// <summary>
    /// Logs when fetching a page of domain registrations.
    /// </summary>
    [LoggerMessage(
        EventId = 50004,
        Level = LogLevel.Debug,
        Message = "Fetching page {Page} of domain registrations")]
    public static partial void FetchingDomainPage(
        this ILogger logger,
        int page);

    /// <summary>
    /// Logs when a domain is not found in the bulk cache.
    /// </summary>
    [LoggerMessage(
        EventId = 50005,
        Level = LogLevel.Warning,
        Message = "Domain {Domain} not found in bulk cache")]
    public static partial void DomainNotFoundInCache(
        this ILogger logger,
        string domain);

    /// <summary>
    /// Logs when querying a single domain from the discovery registry.
    /// </summary>
    [LoggerMessage(
        EventId = 50006,
        Level = LogLevel.Information,
        Message = "Querying single domain '{Domain}' from discovery registry")]
    public static partial void QueryingSingleDomain(
        this ILogger logger,
        string domain);

    /// <summary>
    /// Logs when a domain is successfully resolved from the registry.
    /// </summary>
    [LoggerMessage(
        EventId = 50007,
        Level = LogLevel.Information,
        Message = "Domain '{Domain}' resolved from registry: {BaseUrl}")]
    public static partial void DomainResolvedFromRegistry(
        this ILogger logger,
        string domain,
        string baseUrl);

    #endregion

    #region Specification Validation

    /// <summary>
    /// Logs when validation is bypassed by a specification (Resume, Active SubFlow).
    /// </summary>
    [LoggerMessage(
        EventId = 50010,
        Level = LogLevel.Debug,
        Message = "Validation bypassed by {SpecificationName} for instance {InstanceId}")]
    public static partial void ValidationBypassedBySpecification(
        this ILogger logger,
        string specificationName,
        Guid instanceId);

    /// <summary>
    /// Logs when validation fails by a specification.
    /// </summary>
    [LoggerMessage(
        EventId = 50011,
        Level = LogLevel.Warning,
        Message = "Validation failed by {SpecificationName} for instance {InstanceId}: {ErrorCode} - {ErrorMessage}")]
    public static partial void ValidationFailedBySpecification(
        this ILogger logger,
        string specificationName,
        Guid instanceId,
        string errorCode,
        string errorMessage);

    #endregion

    #region Cache Invalidation

    /// <summary>
    /// Logs when a definition cache invalidation request is received via broadcast.
    /// </summary>
    [LoggerMessage(
        EventId = 50020,
        Level = LogLevel.Information,
        Message = "Definition cache invalidation received. PodInstance: {PodInstance}, Domain: {Domain}, RequestedBy: {RequestedBy}")]
    public static partial void DefinitionCacheInvalidationReceived(
        this ILogger logger,
        string podInstance,
        string domain,
        string requestedBy);

    /// <summary>
    /// Logs when a definition cache invalidation request is ignored due to domain mismatch.
    /// </summary>
    [LoggerMessage(
        EventId = 50021,
        Level = LogLevel.Debug,
        Message = "Definition cache invalidation ignored - domain mismatch. PodInstance: {PodInstance}, Domain: {Domain}")]
    public static partial void DefinitionCacheInvalidationIgnoredDomainMismatch(
        this ILogger logger,
        string podInstance,
        string domain);

    /// <summary>
    /// Logs when definition cache invalidation succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 50022,
        Level = LogLevel.Information,
        Message = "Definition cache invalidation succeeded. PodInstance: {PodInstance}")]
    public static partial void DefinitionCacheInvalidationSucceeded(
        this ILogger logger,
        string podInstance);

    /// <summary>
    /// Logs when definition cache invalidation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 50023,
        Level = LogLevel.Error,
        Message = "Definition cache invalidation failed. PodInstance: {PodInstance}, Error: {Error}")]
    public static partial void DefinitionCacheInvalidationFailed(
        this ILogger logger,
        string podInstance,
        string error);

    #endregion

    #region Authorization (authorize / permissions)

    /// <summary>
    /// Logs when authorize system function is invoked.
    /// </summary>
    [LoggerMessage(
        EventId = 50030,
        Level = LogLevel.Information,
        Message = "Authorize request. Domain: {Domain}, Workflow: {Workflow}, Role: {Role}, Allowed: {Allowed}")]
    public static partial void AuthorizeRequest(
        this ILogger logger,
        string domain,
        string workflow,
        string role,
        bool allowed);

    /// <summary>
    /// Logs when authorization matrix system function is invoked.
    /// </summary>
    [LoggerMessage(
        EventId = 50031,
        Level = LogLevel.Information,
        Message = "Authorization matrix request. Domain: {Domain}, Workflow: {Workflow}")]
    public static partial void AuthorizationMatrixRequest(
        this ILogger logger,
        string domain,
        string workflow);

    #endregion

    #region View Resolution

    /// <summary>
    /// Logs when resolving view content from a remote domain via GetInstanceAsync.
    /// </summary>
    [LoggerMessage(
        EventId = 20100,
        Level = LogLevel.Debug,
        Message = "Resolving view from remote domain. ViewDomain: {ViewDomain}, ViewFlow: {ViewFlow}, ViewKey: {ViewKey}, RequestDomain: {RequestDomain}")]
    public static partial void ResolvingViewFromRemoteDomain(
        this ILogger logger,
        string viewDomain,
        string viewFlow,
        string viewKey,
        string requestDomain);

    #endregion

    #region Extensions

    /// <summary>
    /// Logs when extension processing fails but execution continues (non-blocking).
    /// </summary>
    [LoggerMessage(
        EventId = 20101,
        Level = LogLevel.Warning,
        Message = "Extension processing failed but continuing. Error: {ErrorCode}")]
    public static partial void ExtensionProcessingFailedNonBlocking(
        this ILogger logger,
        string errorCode);

    #endregion

    #region Incidents

    /// <summary>
    /// Logs when an incident is recorded on an instance.
    /// </summary>
    [LoggerMessage(
        EventId = 20200,
        Level = LogLevel.Warning,
        Message = "Incident recorded on instance {InstanceId}. State: {State}, Transition: {Transition}, ErrorCode: {ErrorCode}, Action: {BoundaryAction}")]
    public static partial void IncidentRecorded(
        this ILogger logger,
        Guid instanceId,
        string state,
        string transition,
        string errorCode,
        string? boundaryAction);

    /// <summary>
    /// Logs when an incident is resolved (via retry or successful error-boundary transition).
    /// </summary>
    [LoggerMessage(
        EventId = 20201,
        Level = LogLevel.Information,
        Message = "Incident resolved on instance {InstanceId}. IncidentId: {IncidentId}")]
    public static partial void IncidentResolved(
        this ILogger logger,
        Guid instanceId,
        Guid incidentId);

    /// <summary>
    /// Logs when an informational incident is recorded (Log/Ignore action - already resolved).
    /// </summary>
    [LoggerMessage(
        EventId = 20202,
        Level = LogLevel.Debug,
        Message = "Informational incident recorded (already resolved) on instance {InstanceId}. ErrorCode: {ErrorCode}, Action: {BoundaryAction}")]
    public static partial void IncidentRecordedInformational(
        this ILogger logger,
        Guid instanceId,
        string errorCode,
        string? boundaryAction);

    #endregion

    #region Long-Poll Termination

    /// <summary>
    /// Logs when the pipeline pauses on state entry for declarative long-poll termination.
    /// </summary>
    [LoggerMessage(
        EventId = 20300,
        Level = LogLevel.Information,
        Message = "Long-poll termination armed on instance {InstanceId} at state {State}; fallback in {FallbackSeconds}s")]
    public static partial void LongPollTerminationArmed(
        this ILogger logger,
        Guid instanceId,
        string state,
        int fallbackSeconds);

    /// <summary>
    /// Logs when a paused pipeline resumes after a long-poll acknowledge (or fallback timeout).
    /// </summary>
    [LoggerMessage(
        EventId = 20301,
        Level = LogLevel.Information,
        Message = "Long-poll acknowledge resumed pipeline for instance {InstanceId}")]
    public static partial void LongPollAckResumed(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when a long-poll acknowledge resume is skipped because the instance is no longer awaiting acknowledge.
    /// </summary>
    [LoggerMessage(
        EventId = 20302,
        Level = LogLevel.Debug,
        Message = "Long-poll acknowledge resume skipped (not awaiting) for instance {InstanceId}")]
    public static partial void LongPollAckResumeSkipped(
        this ILogger logger,
        Guid instanceId);

    /// <summary>
    /// Logs when a long-poll acknowledge resume fails.
    /// </summary>
    [LoggerMessage(
        EventId = 20303,
        Level = LogLevel.Error,
        Message = "Long-poll acknowledge resume failed for instance {InstanceId}: {Reason}")]
    public static partial void LongPollAckResumeFailed(
        this ILogger logger,
        Guid instanceId,
        string reason);

    #endregion

    #region Multi-Channel Notification

    /// <summary>
    /// Logs when a notification channel message is successfully dispatched to a Dapr binding.
    /// </summary>
    [LoggerMessage(
        EventId = 10090,
        Level = LogLevel.Information,
        Message = "Notification channel dispatched. TaskKey={TaskKey}, Channel={Channel}, BindingName={BindingName}, InstanceId={InstanceId}")]
    public static partial void NotificationChannelDispatched(
        this ILogger logger,
        string taskKey,
        string channel,
        string bindingName,
        Guid instanceId);

    /// <summary>
    /// Logs when a notification channel is skipped because the mapping returned null.
    /// </summary>
    [LoggerMessage(
        EventId = 10091,
        Level = LogLevel.Debug,
        Message = "Notification channel skipped (mapping returned null). TaskKey={TaskKey}, Channel={Channel}, InstanceId={InstanceId}")]
    public static partial void NotificationChannelSkipped(
        this ILogger logger,
        string taskKey,
        string channel,
        Guid instanceId);

    /// <summary>
    /// Logs when a notification channel dispatch fails (other channels continue).
    /// </summary>
    [LoggerMessage(
        EventId = 10092,
        Level = LogLevel.Warning,
        Message = "Notification channel failed. TaskKey={TaskKey}, Channel={Channel}, InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void NotificationChannelFailed(
        this ILogger logger,
        string taskKey,
        string channel,
        Guid instanceId,
        string errorMessage);

    /// <summary>
    /// Logs the summary when multi-channel notification dispatch completes.
    /// </summary>
    [LoggerMessage(
        EventId = 10093,
        Level = LogLevel.Information,
        Message = "Notification multi-channel completed. TaskKey={TaskKey}, InstanceId={InstanceId}, Dispatched={DispatchedCount}, Skipped={SkippedCount}, Failed={FailedCount}")]
    public static partial void NotificationMultiChannelCompleted(
        this ILogger logger,
        string taskKey,
        Guid instanceId,
        int dispatchedCount,
        int skippedCount,
        int failedCount);

    /// <summary>
    /// Logs when a state-level notification job is scheduled after the pipeline settles.
    /// </summary>
    [LoggerMessage(
        EventId = 10094,
        Level = LogLevel.Information,
        Message = "State notification scheduled. InstanceId={InstanceId}, State={StateKey}")]
    public static partial void StateNotificationScheduled(
        this ILogger logger,
        Guid instanceId,
        string stateKey);

    /// <summary>
    /// Logs when a state-level notification is successfully dispatched to the state Dapr binding.
    /// </summary>
    [LoggerMessage(
        EventId = 10095,
        Level = LogLevel.Information,
        Message = "State notification dispatched. InstanceId={InstanceId}, BindingName={BindingName}")]
    public static partial void StateNotificationDispatched(
        this ILogger logger,
        Guid instanceId,
        string bindingName);

    /// <summary>
    /// Logs when a state-notify job runs but no notification entry is dispatched
    /// (no state entries on the state, or none matched its rule).
    /// </summary>
    [LoggerMessage(
        EventId = 10096,
        Level = LogLevel.Debug,
        Message = "State notification skipped. InstanceId={InstanceId}, State={StateKey}, Reason={Reason}")]
    public static partial void StateNotificationSkipped(
        this ILogger logger,
        Guid instanceId,
        string stateKey,
        string reason);

    /// <summary>
    /// Logs when a state-level notification dispatch fails.
    /// </summary>
    [LoggerMessage(
        EventId = 10097,
        Level = LogLevel.Warning,
        Message = "State notification failed. InstanceId={InstanceId}, Error={ErrorMessage}")]
    public static partial void StateNotificationFailed(
        this ILogger logger,
        Guid instanceId,
        string errorMessage);

    #endregion
  
    #region Server Configuration

    /// <summary>
    /// Logs the configured Kestrel request header limits at startup.
    /// </summary>
    [LoggerMessage(
        EventId = 60001,
        Level = LogLevel.Information,
        Message = "Kestrel limits configured: MaxRequestHeadersTotalSize={MaxRequestHeadersTotalSize}, MaxRequestHeaderCount={MaxRequestHeaderCount}")]
    public static partial void KestrelLimitsConfigured(
        this ILogger logger,
        int maxRequestHeadersTotalSize,
        int maxRequestHeaderCount);

    #endregion

    #region Scripting Helpers (sandbox)

    /// <summary>
    /// Logs that a referenced helper set was compiled and loaded for a mapping.
    /// </summary>
    [LoggerMessage(
        EventId = 60010,
        Level = LogLevel.Information,
        Message = "Helper set built for mapping: {HelperCount} helper(s) [{HelperKeys}], namespaces=[{Namespaces}]")]
    public static partial void ScriptHelperSetBuilt(
        this ILogger logger,
        int helperCount,
        string helperKeys,
        string namespaces);

    /// <summary>
    /// Logs that a referenced helper set was served from the registry cache (no recompile).
    /// </summary>
    [LoggerMessage(
        EventId = 60011,
        Level = LogLevel.Debug,
        Message = "Helper set served from cache: {HelperCount} helper(s) [{HelperKeys}]")]
    public static partial void ScriptHelperSetCacheHit(
        this ILogger logger,
        int helperCount,
        string helperKeys);

    /// <summary>
    /// Logs that a mapping referenced a helper component that could not be resolved from the store.
    /// </summary>
    [LoggerMessage(
        EventId = 60012,
        Level = LogLevel.Error,
        Message = "Helper component could not be resolved: domain={Domain}, flow={Flow}, key={Key}, version={Version}")]
    public static partial void ScriptHelperReferenceUnresolved(
        this ILogger logger,
        string domain,
        string flow,
        string key,
        string version);

    /// <summary>
    /// Logs that a mapping referenced helpers while the custom-script-helpers feature is disabled.
    /// </summary>
    [LoggerMessage(
        EventId = 60013,
        Level = LogLevel.Error,
        Message = "Mapping references helpers but the custom-script-helpers feature is disabled (Scripting:Helpers:Enabled=false)")]
    public static partial void ScriptHelpersDisabled(
        this ILogger logger);

    /// <summary>
    /// Logs that a script failed the sandbox / compilation gate.
    /// </summary>
    [LoggerMessage(
        EventId = 60014,
        Level = LogLevel.Error,
        Message = "Script compilation rejected by sandbox: {Reason}")]
    public static partial void ScriptSandboxViolation(
        this ILogger logger,
        string reason);

    #endregion
}
