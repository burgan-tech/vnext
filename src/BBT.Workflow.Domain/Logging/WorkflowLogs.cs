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
}
