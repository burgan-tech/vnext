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
    /// <summary>
    /// Logs that a persisted transition job was armed in the scheduler after the instance status lock
    /// was released. Debug: one line per accepted async transition, useful when verifying that the
    /// scheduler call really left the critical section.
    /// </summary>
    [LoggerMessage(
        EventId = 10098,
        Level = LogLevel.Debug,
        Message = "Transition job {JobId} armed after lock release")]
    public static partial void TransitionJobArmedAfterLock(
        this ILogger logger,
        Guid jobId);

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
    /// Logs when transition admission rejects a request because the instance is Busy
    /// (Busy-as-mutex model; surfaces as 409 Instance:100031).
    /// </summary>
    [LoggerMessage(
        EventId = 10135,
        Level = LogLevel.Information,
        Message = "Transition {TransitionKey} rejected: instance {InstanceId} is Busy")]
    public static partial void TransitionRejectedInstanceBusy(
        this ILogger logger,
        Guid instanceId,
        string transitionKey);

    /// <summary>
    /// Logs when an instance is reserved (Active→Busy) under the short status lock. The Busy
    /// flag carries mutual exclusion for the pipeline body and its auto-chain.
    /// </summary>
    [LoggerMessage(
        EventId = 10136,
        Level = LogLevel.Debug,
        Message = "Instance {InstanceId} reserved Busy for transition {TransitionKey}")]
    public static partial void InstanceBusyReserved(
        this ILogger logger,
        Guid instanceId,
        string transitionKey);

    /// <summary>
    /// Logs when an instance status settlement (Busy→Active/Completed/Faulted) commits under
    /// the short status lock.
    /// </summary>
    [LoggerMessage(
        EventId = 10138,
        Level = LogLevel.Debug,
        Message = "Instance {InstanceId} status settled to {Status} under status lock")]
    public static partial void InstanceStatusSettled(
        this ILogger logger,
        Guid instanceId,
        string status);

    /// <summary>
    /// Logs when the short status lock could not be acquired within its bounded retry budget.
    /// </summary>
    [LoggerMessage(
        EventId = 10139,
        Level = LogLevel.Warning,
        Message = "Status lock acquisition failed for {LockKey} after bounded retries")]
    public static partial void StatusLockAcquireFailed(
        this ILogger logger,
        string lockKey);

    /// <summary>
    /// Logs when the compensation that releases an accept-time reservation failed. The
    /// instance stays Busy until job-timeout recovery faults it.
    /// </summary>
    [LoggerMessage(
        EventId = 10140,
        Level = LogLevel.Warning,
        Message = "Failed to release reservation for instance {InstanceId}")]
    public static partial void ReservationReleaseFailed(
        this ILogger logger,
        Exception exception,
        Guid instanceId);

    /// <summary>
    /// Logs when a retry re-entry reuses the ORIGINAL transition record so the task journal
    /// lines up and already-completed tasks are bypassed instead of re-running side effects.
    /// </summary>
    [LoggerMessage(
        EventId = 10147,
        Level = LogLevel.Information,
        Message = "Retry of transition {TransitionKey} on instance {InstanceId} reuses transition record {TransitionRecordId}; completed tasks will be bypassed")]
    public static partial void TransitionRecordReusedForRetry(
        this ILogger logger,
        Guid instanceId,
        Guid transitionRecordId,
        string transitionKey);

    /// <summary>
    /// Logs when an updateData execution hands its satisfied auto transition to a real owner:
    /// the continuation boundary reserved the instance (Active→Busy) and the chained transition
    /// proceeds with full normal behavior.
    /// </summary>
    [LoggerMessage(
        EventId = 10145,
        Level = LogLevel.Information,
        Message = "UpdateData {TransitionKey} on instance {InstanceId} reserved the instance for its auto-transition continuation {NextTransitionKey}")]
    public static partial void UpdateDataContinuationReserved(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string nextTransitionKey);

    /// <summary>
    /// Logs when an updateData execution drops its satisfied auto transition because the
    /// instance could not be reserved (a competing chain owns it). The competing owner is
    /// already advancing; a later updateData re-evaluates the same conditions.
    /// </summary>
    [LoggerMessage(
        EventId = 10146,
        Level = LogLevel.Warning,
        Message = "UpdateData {TransitionKey} on instance {InstanceId} dropped its auto-transition continuation {NextTransitionKey}: {ErrorCode}")]
    public static partial void UpdateDataContinuationDropped(
        this ILogger logger,
        Guid instanceId,
        string transitionKey,
        string nextTransitionKey,
        string? errorCode);

    /// <summary>
    /// Logs when the InstanceData write funnel could not acquire the per-instance FOR UPDATE
    /// row lock within lock_timeout — a concurrent writer held it for the whole wait budget.
    /// </summary>
    [LoggerMessage(
        EventId = 10141,
        Level = LogLevel.Warning,
        Message = "Instance data write lock timed out for instance {InstanceId} after {LockTimeoutMs}ms")]
    public static partial void InstanceDataLockWaitTimeout(
        this ILogger logger,
        Guid instanceId,
        int lockTimeoutMs);

    /// <summary>
    /// Logs when the InstanceData write service could not load the workflow's master schema for
    /// pre-persist validation — the append proceeds unvalidated rather than failing the write.
    /// </summary>
    [LoggerMessage(
        EventId = 10148,
        Level = LogLevel.Warning,
        Message = "Failed to load schema {SchemaKey} for instance data validation: {Error}")]
    public static partial void InstanceDataSchemaLoadFailed(
        this ILogger logger,
        string schemaKey,
        string? error);

    /// <summary>
    /// Logs when the write funnel demoted a stale latest row (written by a concurrent
    /// transaction) under the FOR UPDATE lock before inserting the new head.
    /// </summary>
    [LoggerMessage(
        EventId = 10143,
        Level = LogLevel.Debug,
        Message = "Stale latest InstanceData row demoted for instance {InstanceId} before inserting VersionNo {VersionNo}")]
    public static partial void InstanceDataStaleLatestDemoted(
        this ILogger logger,
        Guid instanceId,
        long versionNo);

    /// <summary>
    /// Logs when an InstanceData write statement was cancelled by statement_timeout.
    /// </summary>
    [LoggerMessage(
        EventId = 10144,
        Level = LogLevel.Warning,
        Message = "Instance data write statement timed out for instance {InstanceId} after {StatementTimeoutMs}ms")]
    public static partial void InstanceDataWriteStatementTimeout(
        this ILogger logger,
        Guid instanceId,
        int statementTimeoutMs);

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
    /// Logs when state-specific scheduled jobs are successfully processed.
    /// </summary>
    [LoggerMessage(
        EventId = 10058,
        Level = LogLevel.Information,
        Message = "Processed {Count} scheduled jobs for instance {InstanceId}, transitions: {TransitionKeys}")]
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

    #region Fan-Out Execution

    /// <summary>
    /// Logs when a fan-out batch begins dispatching its items. Paired with
    /// <see cref="FanOutBatchCompleted"/>: the two together bracket the batch and give the
    /// operator its size and bounds before anything runs.
    /// </summary>
    /// <remarks>
    /// <paramref name="itemAlias"/> is the task's readability label for one item, or a neutral
    /// substitute when it declares none — it is a separate structured field rather than being
    /// folded into the count so a log backend can still facet on it.
    /// </remarks>
    [LoggerMessage(
        EventId = 10150,
        Level = LogLevel.Information,
        Message = "FanOut batch started. TaskKey={TaskKey}, Items={ItemCount} '{ItemAlias}', MaxDop={MaxDegreeOfParallelism}, JoinPolicy={JoinPolicy}, InstanceId={InstanceId}")]
    public static partial void FanOutBatchStarted(
        this ILogger logger,
        string taskKey,
        int itemCount,
        string itemAlias,
        int maxDegreeOfParallelism,
        string joinPolicy,
        Guid instanceId);

    /// <summary>
    /// Logs a single failed fan-out item. Warning, not Error: one failed item is a recoverable
    /// outcome the join policy decides on — only the batch's verdict can fault the transition.
    /// This is the log line that names WHICH item among N went wrong.
    /// It carries the error MESSAGE as well as the code: the message is otherwise only attached to
    /// the item span, and item spans are emitted at Verbose tracing detail only — so at the default
    /// level the reason a fan-out item failed would be unrecoverable from logs.
    /// </summary>
    [LoggerMessage(
        EventId = 10151,
        Level = LogLevel.Warning,
        Message = "FanOut item failed. TaskKey={TaskKey}, ItemKey={ItemKey}, Index={ItemIndex}, ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}, InstanceId={InstanceId}")]
    public static partial void FanOutItemFailed(
        this ILogger logger,
        string taskKey,
        string itemKey,
        int itemIndex,
        string errorCode,
        string? errorMessage,
        Guid instanceId);

    /// <summary>
    /// Logs the settled counters of a fan-out batch, including the wall-clock duration of the
    /// whole batch (queueing included).
    /// </summary>
    [LoggerMessage(
        EventId = 10152,
        Level = LogLevel.Information,
        Message = "FanOut batch completed. TaskKey={TaskKey}, Total={Total}, Succeeded={Succeeded}, Failed={Failed}, DurationMs={DurationMs}, InstanceId={InstanceId}")]
    public static partial void FanOutBatchCompleted(
        this ILogger logger,
        string taskKey,
        int total,
        int succeeded,
        int failed,
        long durationMs,
        Guid instanceId);

    /// <summary>
    /// Logs when a fan-out batch hit its <c>batchTimeoutSeconds</c> deadline, reporting how many
    /// items had settled on their own before the deadline cut the rest short.
    /// </summary>
    [LoggerMessage(
        EventId = 10153,
        Level = LogLevel.Warning,
        Message = "FanOut batch timed out. TaskKey={TaskKey}, Settled={SettledCount}/{Total}, BatchTimeout={BatchTimeoutSeconds}s, InstanceId={InstanceId}")]
    public static partial void FanOutBatchTimedOut(
        this ILogger logger,
        string taskKey,
        int settledCount,
        int total,
        int batchTimeoutSeconds,
        Guid instanceId);

    /// <summary>
    /// Logs when a fan-out batch had to queue behind the process-wide item bulkhead — i.e. the
    /// configured ceiling, not the batch's own <c>maxDegreeOfParallelism</c>, is what is now
    /// limiting throughput. Emitted at most ONCE per batch: every queued item observing the same
    /// saturation would be noise, not signal.
    /// </summary>
    [LoggerMessage(
        EventId = 10154,
        Level = LogLevel.Warning,
        Message = "FanOut global item bulkhead saturated. TaskKey={TaskKey}, Active={ActiveItems}/{MaxConcurrentItems}")]
    public static partial void FanOutBulkheadSaturated(
        this ILogger logger,
        string taskKey,
        int activeItems,
        int maxConcurrentItems);

    #endregion

    #region Task Coordinator

    /// <summary>
    /// Logs when a hook's onExecute/onEntry/onExit task list carries the same task key more than
    /// once at the SAME order. <see cref="TaskCoordinator"/> now gives each occurrence a distinct
    /// journal identity (a positional <c>#index</c> suffix), so this no longer faults the instance —
    /// but two entries sharing both key and order is still almost certainly an authoring mistake
    /// (duplicated line, copy-paste) rather than an intentional design, so it is surfaced as a
    /// Warning for the author to fix, not rejected. <see cref="WorkflowValidationResult"/> only
    /// carries hard errors (see <c>ValidationErrors</c>/<c>AddError</c>) with no warning severity,
    /// so this is logged here at execution time instead of being folded into
    /// <c>WorkflowValidator</c> — do not downgrade this to a validation error.
    /// <c>TaskCoordinator.LogDuplicateTaskKeysIfAny</c> gates this off of
    /// <see cref="TaskExecutionOrigin"/>, NOT <see cref="TaskTrigger"/>: it never calls this for
    /// <see cref="TaskExecutionOrigin.Extension"/> (two extensions sharing one task Reference is a
    /// supported pattern — each carries its own <c>Mapping</c> and files its output under its own
    /// key, not the task's; this warning's remedy of "give the entries distinct orders" targets a
    /// journal-key collision that cannot happen there, since
    /// <c>ExtensionTaskPersistenceStrategy</c> never persists an <c>InstanceTask</c> row for
    /// Extension-origin executions at all). Custom functions execute through
    /// <c>TaskTrigger.Extension</c> too (<c>FunctionAppService.cs</c>) but with
    /// <see cref="TaskExecutionOrigin.Function"/> — a multi-task function
    /// (<c>FunctionAppService.GetSingleTaskVariableKey</c>) listing the same task twice at the same
    /// order is still an authoring mistake with no per-entry response-key override to save it, so
    /// this warning MUST still fire for that shape. Gating on the trigger instead of the origin
    /// would silently swallow it.
    /// </summary>
    [LoggerMessage(
        EventId = 10155,
        Level = LogLevel.Warning,
        Message = "Duplicate task key at the same order in transition {TransitionKey}, hook {Hook}: task '{TaskKey}' appears {OccurrenceCount} times at order {Order}. This is usually an authoring mistake — give the entries distinct orders if they are meant to run as separate steps. InstanceId={InstanceId}")]
    public static partial void DuplicateTaskKeyAtSameOrder(
        this ILogger logger,
        string transitionKey,
        string hook,
        string taskKey,
        int occurrenceCount,
        int order,
        Guid? instanceId);

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

    [LoggerMessage(
        EventId = 40031,
        Level = LogLevel.Debug,
        Message = "Duplicate {Outcome} SubItem terminal outcome for parent {ParentInstanceId}, child {SubInstanceId}")]
    public static partial void SubItemTerminalDuplicate(
        this ILogger logger,
        string outcome,
        Guid parentInstanceId,
        Guid subInstanceId);

    [LoggerMessage(
        EventId = 40053,
        Level = LogLevel.Warning,
        Message = "SubItem terminal outcome conflict for parent {ParentInstanceId}, child {SubInstanceId}: existing {ExistingOutcome}, incoming {IncomingOutcome}")]
    public static partial void SubItemTerminalConflict(
        this ILogger logger,
        Guid parentInstanceId,
        Guid subInstanceId,
        string existingOutcome,
        string incomingOutcome);

    [LoggerMessage(
        EventId = 40054,
        Level = LogLevel.Warning,
        Message = "Failed to revert terminal SubItem correlation for parent {ParentInstanceId}, child {SubInstanceId}")]
    public static partial void SubItemCorrelationRevertFailed(
        this ILogger logger,
        Exception exception,
        Guid parentInstanceId,
        Guid subInstanceId);

    [LoggerMessage(
        EventId = 40055,
        Level = LogLevel.Warning,
        Message = "Parent terminal lock {LockKey} could not be acquired for {Outcome} outcome")]
    public static partial void SubItemTerminalLockNotAcquired(
        this ILogger logger,
        string lockKey,
        string outcome);

    /// <summary>
    /// Logs when a duplicate terminal delivery is short-circuited before the distributed lock is
    /// taken, because the identical outcome is already persisted on the correlation.
    /// </summary>
    [LoggerMessage(
        EventId = 40056,
        Level = LogLevel.Debug,
        Message = "Duplicate {Outcome} SubItem terminal delivery skipped pre-lock for parent {ParentInstanceId}, child {SubInstanceId}")]
    public static partial void SubItemTerminalDuplicateSkippedPreLock(
        this ILogger logger,
        string outcome,
        Guid parentInstanceId,
        Guid subInstanceId);

    /// <summary>
    /// Logs when the pre-lock fast path is declined because settlement cannot be proven from the
    /// snapshot — a blocking SubFlow settles only after its second-phase parent resume succeeds.
    /// </summary>
    [LoggerMessage(
        EventId = 40059,
        Level = LogLevel.Debug,
        Message = "SubItem terminal settlement not provable for {SubItemType} (parent {ParentInstanceId}, child {SubInstanceId}); using locked path")]
    public static partial void SubItemTerminalSettlementNotProvable(
        this ILogger logger,
        string subItemType,
        Guid parentInstanceId,
        Guid subInstanceId);

    /// <summary>
    /// Logs when the lock-free terminal probe could not read the correlation snapshot. The caller
    /// falls back to the authoritative locked path, so this is not a failure of the delivery.
    /// </summary>
    [LoggerMessage(
        EventId = 40057,
        Level = LogLevel.Debug,
        Message = "SubItem terminal pre-lock probe failed for parent {ParentInstanceId}, child {SubInstanceId}; falling back to locked path")]
    public static partial void SubItemTerminalProbeFailed(
        this ILogger logger,
        Exception exception,
        Guid parentInstanceId,
        Guid subInstanceId);

    /// <summary>
    /// Logs when a contended transition lock acquisition is retried after a jittered backoff.
    /// </summary>
    [LoggerMessage(
        EventId = 40058,
        Level = LogLevel.Debug,
        Message = "Transition lock {LockKey} busy (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs}ms")]
    public static partial void TransitionLockRetryScheduled(
        this ILogger logger,
        string lockKey,
        int attempt,
        int maxAttempts,
        int delayMs);

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

    /// <summary>
    /// Logs when a subflow terminal event (completed/faulted/canceled) is relayed to the parent
    /// instance as an immediate post-commit command (Outbox + TerminalRelay mode).
    /// </summary>
    [LoggerMessage(
        EventId = 40124,
        Level = LogLevel.Information,
        Message = "Subflow terminal {EventName} relayed to parent (sub {SubInstanceId} -> parent {ParentInstanceId})")]
    public static partial void SubflowTerminalRelayed(
        this ILogger logger,
        string eventName,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a subflow terminal relay attempt throws. The child's commit already stands, so the
    /// durable Inbox backup will settle the parent shortly after.
    /// </summary>
    [LoggerMessage(
        EventId = 40125,
        Level = LogLevel.Warning,
        Message = "Subflow terminal relay failed for {EventName} (sub {SubInstanceId} -> parent {ParentInstanceId}); Inbox backup will settle")]
    public static partial void SubflowTerminalRelayFailed(
        this ILogger logger,
        Exception exception,
        string eventName,
        Guid subInstanceId,
        Guid parentInstanceId);

    /// <summary>
    /// Logs when a subflow terminal relay's gateway call returns a failed <c>Result</c> (not an
    /// exception). The durable Inbox backup will settle the parent shortly after.
    /// </summary>
    [LoggerMessage(
        EventId = 40126,
        Level = LogLevel.Warning,
        Message = "Subflow terminal relay rejected for {EventName}: {Error}; Inbox backup will settle")]
    public static partial void SubflowTerminalRelayRejected(
        this ILogger logger,
        string eventName,
        string error);

    /// <summary>
    /// Logs when a terminal-revert re-publishes the subflow terminal event as a durable-delivery
    /// rearm, inside the same UoW as the revert. Closes the window where the lock-free duplicate
    /// ACK consumed the original durable delivery before a phase-2 resume failure reopened the
    /// correlation.
    /// </summary>
    [LoggerMessage(
        EventId = 40127,
        Level = LogLevel.Warning,
        Message = "Subflow terminal settlement reverted; durable delivery re-armed (attempt {Attempt}) for sub {SubInstanceId} -> parent {ParentInstanceId}")]
    public static partial void SubflowTerminalRearmed(
        this ILogger logger,
        Guid parentInstanceId,
        Guid subInstanceId,
        int attempt);

    /// <summary>
    /// Logs when a terminal-revert's rearm budget is exhausted — the correlation was reverted but
    /// no fresh durable delivery was published, so manual intervention may be required.
    /// </summary>
    [LoggerMessage(
        EventId = 40128,
        Level = LogLevel.Error,
        Message = "Subflow terminal re-arm budget exhausted ({Attempt}) for sub {SubInstanceId} -> parent {ParentInstanceId}; manual intervention required")]
    public static partial void SubflowTerminalRearmExhausted(
        this ILogger logger,
        Guid parentInstanceId,
        Guid subInstanceId,
        int attempt);

    #endregion

    #region Instance Management

    /// <summary>
    /// Logs the successful start of a workflow instance. Emitted while the start HTTP request is
    /// still live so the log-record enrichers attach the request id — this record is the join
    /// between the client's request (X-Request-Id) and the newly generated instance id.
    /// </summary>
    [LoggerMessage(
        EventId = 20008,
        Level = LogLevel.Information,
        Message = "Instance {InstanceId} (key: {InstanceKey}) started for {Domain}/{Flow} v{FlowVersion}")]
    public static partial void InstanceStarted(
        this ILogger logger,
        Guid instanceId,
        string? instanceKey,
        string domain,
        string flow,
        string? flowVersion);

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
    /// Logs when an async transition request is rejected because the instance is Busy
    /// (a transition is already queued or executing).
    /// </summary>
    [LoggerMessage(
        EventId = 40120,
        Level = LogLevel.Warning,
        Message = "Async transition {TransitionKey} rejected for instance {InstanceId}: instance is busy")]
    public static partial void AsyncTransitionRejectedInstanceBusy(
        this ILogger logger,
        string transitionKey,
        Guid instanceId);

    /// <summary>
    /// Logs a transient instance-lock conflict inside a transition job; the handler retries with backoff.
    /// </summary>
    [LoggerMessage(
        EventId = 40121,
        Level = LogLevel.Warning,
        Message = "Transition job {JobName} hit instance lock conflict for instance {InstanceId} (attempt {Attempt}/{MaxAttempts}); retrying in {DelayMs}ms")]
    public static partial void TransitionJobLockConflictRetry(
        this ILogger logger,
        string jobName,
        Guid instanceId,
        int attempt,
        int maxAttempts,
        int delayMs);

    /// <summary>
    /// Logs when a transition job exhausted all lock-conflict retries; the instance is routed to recovery (Faulted).
    /// </summary>
    [LoggerMessage(
        EventId = 40122,
        Level = LogLevel.Error,
        Message = "Transition job {JobName} exhausted {MaxAttempts} lock-conflict retries for instance {InstanceId} on transition {TransitionKey}; faulting instance")]
    public static partial void TransitionJobLockConflictRetriesExhausted(
        this ILogger logger,
        string jobName,
        int maxAttempts,
        Guid instanceId,
        string transitionKey);

    /// <summary>
    /// Logs when a lock acquisition is satisfied reentrantly because the current execution
    /// chain already holds the same key (e.g. sync subflow completion inside the parent's
    /// post-commit phase).
    /// </summary>
    [LoggerMessage(
        EventId = 40123,
        Level = LogLevel.Debug,
        Message = "Transition lock {LockKey} already held by the current execution chain; acquired reentrantly")]
    public static partial void TransitionLockReentrantAcquired(
        this ILogger logger,
        string lockKey);

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
        Message = "Processed {JobCount} instance cancellation jobs for instance {InstanceId}")]
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

    /// <summary>
    /// Logs when cleanup leaves a running background job to its dispatcher.
    /// </summary>
    [LoggerMessage(
        EventId = 40104,
        Level = LogLevel.Information,
        Message = "Background job {JobId} is already running for instance {InstanceId}; cleanup left it to the dispatcher")]
    public static partial void InstanceJobCleanupSkippedRunning(
        this ILogger logger,
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
    /// Logs when a resource lock release is a no-op because the lock no longer exists
    /// (TTL already expired or it was never acquired). Treated as an idempotent success.
    /// </summary>
    [LoggerMessage(
        EventId = 10102,
        Level = LogLevel.Debug,
        Message = "Resource lock release no-op (lock does not exist): Key={ResourceKey}, Owner={Owner}")]
    public static partial void ResourceLockReleaseNoop(
        this ILogger logger,
        string resourceKey,
        string owner);

    /// <summary>
    /// Logs when a resource lock release hits a genuine anomaly (lock held by another owner
    /// or an infrastructure error). Best-effort cleanup: surfaced for metrics, does not fault the caller.
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
    /// Logs when the automatic terminal-cleanup release of a tracked resource lock throws.
    /// Best-effort: the failure is swallowed (TTL is the safety net) and only surfaced for metrics.
    /// </summary>
    [LoggerMessage(
        EventId = 10105,
        Level = LogLevel.Warning,
        Message = "Resource lock auto-release errored: Key={ResourceKey}, InstanceId={InstanceId}")]
    public static partial void ResourceLockAutoReleaseError(
        this ILogger logger,
        Exception exception,
        string resourceKey,
        Guid instanceId);

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

    /// <summary>
    /// Logs when this pod skipped domain registration because it did not acquire the
    /// once-per-rollout registration lock. Another replica already owns (or will own) it; this
    /// pod starts normally without registering.
    /// </summary>
    [LoggerMessage(
        EventId = 50008,
        Level = LogLevel.Information,
        Message = "Domain registration skipped for '{Domain}' - lock not acquired, another replica owns this rollout's registration")]
    public static partial void DomainRegistrationSkippedNotLockOwner(
        this ILogger logger,
        string domain);

    /// <summary>
    /// Logs when this pod acquired the registration lock and will perform the registration.
    /// </summary>
    [LoggerMessage(
        EventId = 50009,
        Level = LogLevel.Information,
        Message = "Domain registration lock acquired for '{Domain}' - this pod will register")]
    public static partial void DomainRegistrationClaimed(
        this ILogger logger,
        string domain);

    /// <summary>
    /// Logs when domain registration is skipped entirely (no lock attempt, no registration call)
    /// because service discovery is disabled for this pod.
    /// </summary>
    [LoggerMessage(
        EventId = 50012,
        Level = LogLevel.Debug,
        Message = "Domain registration skipped for '{Domain}' - service discovery is disabled")]
    public static partial void DomainRegistrationSkippedDisabled(
        this ILogger logger,
        string domain);

    /// <summary>
    /// Logs when the domain discovery initialization hosted service starts running.
    /// </summary>
    [LoggerMessage(
        EventId = 50013,
        Level = LogLevel.Information,
        Message = "Starting domain discovery initialization...")]
    public static partial void DomainDiscoveryInitializationStarted(
        this ILogger logger);

    /// <summary>
    /// Logs when the domain discovery initialization hosted service completes successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 50014,
        Level = LogLevel.Information,
        Message = "Domain discovery initialization completed successfully")]
    public static partial void DomainDiscoveryInitializationSucceeded(
        this ILogger logger);

    /// <summary>
    /// Logs when domain discovery initialization fails. This is always fatal to startup: the
    /// caller rethrows so the host aborts and the pod is restarted.
    /// </summary>
    [LoggerMessage(
        EventId = 50015,
        Level = LogLevel.Critical,
        Message = "Domain discovery initialization failed. Application startup will be aborted.")]
    public static partial void DomainDiscoveryInitializationFailed(
        this ILogger logger,
        Exception exception);

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

    #region Event-driven transitions

    /// <summary>
    /// Logs when an external event is received for a workflow.
    /// </summary>
    [LoggerMessage(
        EventId = 40990,
        Level = LogLevel.Information,
        Message = "Event received. Domain: {Domain}, Workflow: {Workflow}, Action: {Action}, TransitionKey: {TransitionKey}")]
    public static partial void EventReceived(
        this ILogger logger,
        string domain,
        string workflow,
        string action,
        string? transitionKey);

    /// <summary>
    /// Logs when an event refers to a workflow/transition that has no event definition.
    /// </summary>
    [LoggerMessage(
        EventId = 40991,
        Level = LogLevel.Warning,
        Message = "Event has no matching event definition. Domain: {Domain}, Workflow: {Workflow}, TransitionKey: {TransitionKey}")]
    public static partial void EventDefinitionMissing(
        this ILogger logger,
        string domain,
        string workflow,
        string? transitionKey);

    /// <summary>
    /// Logs when an event-transition cannot be correlated to an active instance and is therefore ignored.
    /// </summary>
    [LoggerMessage(
        EventId = 40992,
        Level = LogLevel.Information,
        Message = "Event ignored - no active instance for key. Domain: {Domain}, Workflow: {Workflow}, InstanceKey: {InstanceKey}, TransitionKey: {TransitionKey}")]
    public static partial void EventInstanceNotFoundIgnored(
        this ILogger logger,
        string domain,
        string workflow,
        string? instanceKey,
        string? transitionKey);

    /// <summary>
    /// Logs when the event mapping fails to compile or execute.
    /// </summary>
    [LoggerMessage(
        EventId = 40993,
        Level = LogLevel.Error,
        Message = "Event mapping failed. Domain: {Domain}, Workflow: {Workflow}, TransitionKey: {TransitionKey}, Error: {Error}")]
    public static partial void EventMappingFailed(
        this ILogger logger,
        string domain,
        string workflow,
        string? transitionKey,
        string error);

    /// <summary>
    /// Logs when an event delivery is discarded because it can never be processed, so the response
    /// signals Dapr to DROP the message rather than block the partition with endless redelivery.
    /// </summary>
    [LoggerMessage(
        EventId = 40994,
        Level = LogLevel.Warning,
        Message = "Event delivery dropped (Dapr DROP). Domain: {Domain}, Workflow: {Workflow}, TransitionKey: {TransitionKey}, Code: {ErrorCode}, Reason: {Reason}")]
    public static partial void EventDeliveryDropped(
        this ILogger logger,
        string? domain,
        string? workflow,
        string? transitionKey,
        string? errorCode,
        string? reason);

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

    /// <summary>
    /// Logs when the SAME extension reference is listed more than once in a workflow's
    /// <c>Extensions</c> (or in the runtime's core-extension set). Unlike two DIFFERENT extensions
    /// sharing one task Reference (a supported pattern, see <see cref="DuplicateTaskKeyAtSameOrder"/>
    /// remarks), this is the SAME <c>Extension</c> — and therefore the SAME <c>OnExecuteTask</c>
    /// instance — appearing twice. <c>InstanceExtensionService.ExecuteExtensionsInternalAsync</c>'s
    /// last-wins <c>responseKeyByTask</c> build detects this for free: the key it is about to write
    /// is already present. The task still executes once per occurrence for the one output slot and
    /// can still throw the parallel-merge conflict this whole fix exists to prevent, so unlike the
    /// task-coordinator warning above, the remedy here is NOT "give them distinct orders" — the
    /// sequential path silently overwrites at <c>ScriptContext.SetOutputResponse</c> regardless of
    /// order, so distinct orders would not fix this shape. The only correct remedy is removing the
    /// duplicate reference.
    /// </summary>
    [LoggerMessage(
        EventId = 20102,
        Level = LogLevel.Warning,
        Message = "Duplicate extension reference '{ExtensionKey}' in workflow '{WorkflowKey}': the same Extension is listed more than once, so its task executes once per occurrence for one output slot and the merge can still throw a parallel-output conflict. Remove the duplicate reference — giving the entries distinct orders does not fix this, the sequential path overwrites silently regardless of order.")]
    public static partial void DuplicateExtensionReference(
        this ILogger logger,
        string extensionKey,
        string workflowKey);

    /// <summary>
    /// Logs when <c>InstanceExtensionService</c>'s per-task <c>optionsRefiner</c> cannot find the
    /// executing task in its <c>responseKeyByTask</c> map, so <c>ResponseVariableKey</c> falls back
    /// to <c>null</c>. <c>TaskCoordinator</c> only ever hands back the SAME <c>OnExecuteTask</c>
    /// instances it was given (ToList/Where/GroupBy, never cloned), so this branch is unreachable
    /// today — but a null <c>ResponseVariableKey</c> makes the task's output file under the
    /// task-derived key instead of the extension's, and <c>ExtractExtensionResponse</c> only ever
    /// reads by the EXTENSION's key — so the extension's result is silently dropped, exactly the
    /// silent-data-loss class the extension-response-key fix exists to eliminate. Logged so this
    /// cannot pass unnoticed if the assumption it depends on is ever broken.
    /// </summary>
    [LoggerMessage(
        EventId = 20103,
        Level = LogLevel.Warning,
        Message = "Extension task '{TaskKey}' had no entry in the per-extension response-key map; falling back to the task-derived key. This extension's own read will not find it there, so its output is silently dropped. InstanceId={InstanceId}")]
    public static partial void ExtensionResponseKeyMappingMissing(
        this ILogger logger,
        string taskKey,
        Guid? instanceId);

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

    #region State Function Cache

    /// <summary>
    /// Logs when a state-function response is served from cache (fingerprint validated as unchanged).
    /// </summary>
    [LoggerMessage(
        EventId = 20400,
        Level = LogLevel.Debug,
        Message = "State function cache hit for instance {Instance} (state {State}, status {Status})")]
    public static partial void StateFunctionCacheHit(
        this ILogger logger,
        string instance,
        string? state,
        string status);

    /// <summary>
    /// Logs when no cached state-function response exists for the caller scope.
    /// </summary>
    [LoggerMessage(
        EventId = 20401,
        Level = LogLevel.Debug,
        Message = "State function cache miss for instance {Instance}")]
    public static partial void StateFunctionCacheMiss(
        this ILogger logger,
        string instance);

    /// <summary>
    /// Logs when a cached state-function response is discarded because its fingerprint ETag
    /// no longer matches the ETag computed from the current projection.
    /// </summary>
    [LoggerMessage(
        EventId = 20402,
        Level = LogLevel.Debug,
        Message = "State function cache invalidated for instance {Instance}: etag {CachedEtag} -> {CurrentEtag}")]
    public static partial void StateFunctionCacheInvalidated(
        this ILogger logger,
        string instance,
        string? cachedEtag,
        string currentEtag);

    /// <summary>
    /// Logs when the cache is bypassed because the instance has an active SubFlow —
    /// the state response is built from a live subflow call and cannot be validated locally.
    /// </summary>
    [LoggerMessage(
        EventId = 20403,
        Level = LogLevel.Debug,
        Message = "State function cache bypassed for instance {Instance}: active SubFlow requires live evaluation")]
    public static partial void StateFunctionCacheBypassedForSubFlow(
        this ILogger logger,
        string instance);

    /// <summary>
    /// Logs when a state-function cache operation fails; the failure degrades to a miss.
    /// </summary>
    [LoggerMessage(
        EventId = 20404,
        Level = LogLevel.Warning,
        Message = "State function cache {Operation} failed for key {CacheKey}; treating as miss")]
    public static partial void StateFunctionCacheError(
        this ILogger logger,
        Exception exception,
        string operation,
        string cacheKey);

    /// <summary>
    /// Logs when the fingerprint ETag matched the caller's If-None-Match and 304 was returned
    /// directly from the projection query — no cache access, aggregate load or response build.
    /// </summary>
    [LoggerMessage(
        EventId = 20405,
        Level = LogLevel.Debug,
        Message = "State function ETag unchanged for instance {Instance} (state {State}, status {Status}); returning 304 from fingerprint")]
    public static partial void StateFunctionEtagNotModified(
        this ILogger logger,
        string instance,
        string? state,
        string status);

    #endregion

    #region Data Function Cache

    /// <summary>
    /// Logs when a data-function response is served from cache (fingerprint ETag validated).
    /// </summary>
    [LoggerMessage(
        EventId = 20410,
        Level = LogLevel.Debug,
        Message = "Data function cache hit for instance {Instance}")]
    public static partial void DataFunctionCacheHit(
        this ILogger logger,
        string instance);

    /// <summary>
    /// Logs when no cached data-function response exists for the caller scope.
    /// </summary>
    [LoggerMessage(
        EventId = 20411,
        Level = LogLevel.Debug,
        Message = "Data function cache miss for instance {Instance}")]
    public static partial void DataFunctionCacheMiss(
        this ILogger logger,
        string instance);

    /// <summary>
    /// Logs when a cached data-function response is discarded because its fingerprint ETag
    /// no longer matches the ETag computed from the current projection.
    /// </summary>
    [LoggerMessage(
        EventId = 20412,
        Level = LogLevel.Debug,
        Message = "Data function cache invalidated for instance {Instance}: etag {CachedEtag} -> {CurrentEtag}")]
    public static partial void DataFunctionCacheInvalidated(
        this ILogger logger,
        string instance,
        string? cachedEtag,
        string currentEtag);

    /// <summary>
    /// Logs when a data-function cache operation fails; the failure degrades to a miss.
    /// </summary>
    [LoggerMessage(
        EventId = 20413,
        Level = LogLevel.Warning,
        Message = "Data function cache {Operation} failed for key {CacheKey}; treating as miss")]
    public static partial void DataFunctionCacheError(
        this ILogger logger,
        Exception exception,
        string operation,
        string cacheKey);

    /// <summary>
    /// Logs when the data fingerprint ETag matched the caller's If-None-Match and 304 was
    /// returned from the projection query — no cache access, aggregate load, extension run
    /// or response build.
    /// </summary>
    [LoggerMessage(
        EventId = 20414,
        Level = LogLevel.Debug,
        Message = "Data function ETag unchanged for instance {Instance}; returning 304 from fingerprint")]
    public static partial void DataFunctionEtagNotModified(
        this ILogger logger,
        string instance);

    #endregion

    #region Master/Schema Function Cache

    // One shared quintet serves both the master and the schema function ({Function} parameter
    // is "master" or "schema") — they share the cache service and the response body shape.

    /// <summary>
    /// Logs when a master/schema response is served from cache (fingerprint ETag validated).
    /// </summary>
    [LoggerMessage(
        EventId = 20420,
        Level = LogLevel.Debug,
        Message = "{Function} function cache hit for instance {Instance}")]
    public static partial void InstanceSchemaFunctionCacheHit(
        this ILogger logger,
        string function,
        string instance);

    /// <summary>
    /// Logs when no cached master/schema response exists for the caller scope.
    /// </summary>
    [LoggerMessage(
        EventId = 20421,
        Level = LogLevel.Debug,
        Message = "{Function} function cache miss for instance {Instance}")]
    public static partial void InstanceSchemaFunctionCacheMiss(
        this ILogger logger,
        string function,
        string instance);

    /// <summary>
    /// Logs when a cached master/schema response is discarded because its fingerprint ETag
    /// no longer matches the ETag computed from the current projection.
    /// </summary>
    [LoggerMessage(
        EventId = 20422,
        Level = LogLevel.Debug,
        Message = "{Function} function cache invalidated for instance {Instance}: etag {CachedEtag} -> {CurrentEtag}")]
    public static partial void InstanceSchemaFunctionCacheInvalidated(
        this ILogger logger,
        string function,
        string instance,
        string? cachedEtag,
        string currentEtag);

    /// <summary>
    /// Logs when a master/schema cache operation fails; the failure degrades to a miss.
    /// </summary>
    [LoggerMessage(
        EventId = 20423,
        Level = LogLevel.Warning,
        Message = "{Function} function cache {Operation} failed for key {CacheKey}; treating as miss")]
    public static partial void InstanceSchemaFunctionCacheError(
        this ILogger logger,
        Exception exception,
        string function,
        string operation,
        string cacheKey);

    /// <summary>
    /// Logs when the fingerprint ETag matched the caller's If-None-Match and 304 was returned
    /// from the projection query — no cache access, aggregate load or response build.
    /// </summary>
    [LoggerMessage(
        EventId = 20424,
        Level = LogLevel.Debug,
        Message = "{Function} function ETag unchanged for instance {Instance}; returning 304 from fingerprint")]
    public static partial void InstanceSchemaFunctionEtagNotModified(
        this ILogger logger,
        string function,
        string instance);

    /// <summary>
    /// Logs when the fast path and cache are bypassed because the instance has an active
    /// SubFlow — the master/schema response is composed from a live subflow call.
    /// </summary>
    [LoggerMessage(
        EventId = 20425,
        Level = LogLevel.Debug,
        Message = "{Function} function cache bypassed for instance {Instance}: active SubFlow requires live evaluation")]
    public static partial void InstanceSchemaFunctionCacheBypassedForSubFlow(
        this ILogger logger,
        string function,
        string instance);

    #endregion

    #region Related Instance Access

    /// <summary>
    /// Logs when a related instance (parent or correlation) was read successfully.
    /// </summary>
    [LoggerMessage(
        EventId = 20430,
        Level = LogLevel.Debug,
        Message = "Related instance resolved. Instance: {InstanceId}, Direction: {Direction}, Target: {TargetInstanceId}, Domain: {TargetDomain}, Flow: {TargetFlow}")]
    public static partial void RelatedInstanceResolved(
        this ILogger logger,
        Guid instanceId,
        string direction,
        Guid targetInstanceId,
        string targetDomain,
        string targetFlow);

    /// <summary>
    /// Logs when no related instance could be resolved (no parent, no matching correlation,
    /// or the target instance is gone). This is a normal outcome, not an error.
    /// </summary>
    [LoggerMessage(
        EventId = 20431,
        Level = LogLevel.Debug,
        Message = "Related instance not found. Instance: {InstanceId}, Direction: {Direction}, Key: {Key}")]
    public static partial void RelatedInstanceNotFound(
        this ILogger logger,
        Guid instanceId,
        string direction,
        string? key);

    /// <summary>
    /// Logs when resolving a related instance required a cross-domain read over HTTP. Logged by
    /// <see cref="BBT.Workflow.Gateway.RoutedRelatedInstanceReader"/>, which only sees the target of the
    /// dispatch (a <c>RelatedInstanceRef</c>), not the instance whose script triggered the read — hence
    /// <paramref name="targetInstanceId"/> identifies the instance being read, not the reader.
    /// </summary>
    [LoggerMessage(
        EventId = 20432,
        Level = LogLevel.Debug,
        Message = "Related instance cross-domain read. Target: {TargetInstanceId}, TargetDomain: {TargetDomain}, TargetFlow: {TargetFlow}, Count: {Count}")]
    public static partial void RelatedInstanceCrossDomainRead(
        this ILogger logger,
        Guid targetInstanceId,
        string targetDomain,
        string targetFlow,
        int count);

    /// <summary>
    /// Logs when resolving a related instance failed due to an infrastructure problem;
    /// the accessor throws after logging this.
    /// </summary>
    [LoggerMessage(
        EventId = 20433,
        Level = LogLevel.Error,
        Message = "Related instance resolution failed. Instance: {InstanceId}, Direction: {Direction}, Target: {TargetInstanceId}, TargetDomain: {TargetDomain}, TargetFlow: {TargetFlow}, Reason: {Reason}")]
    public static partial void RelatedInstanceResolutionFailed(
        this ILogger logger,
        Guid instanceId,
        string direction,
        Guid targetInstanceId,
        string targetDomain,
        string targetFlow,
        string reason);

    /// <summary>
    /// Logs when the per-ScriptContext related-instance resolution cap was hit;
    /// the accessor throws after logging this.
    /// </summary>
    [LoggerMessage(
        EventId = 20434,
        Level = LogLevel.Warning,
        Message = "Related instance resolution limit exceeded. Instance: {InstanceId}, Limit: {Limit}")]
    public static partial void RelatedInstanceResolutionLimitExceeded(
        this ILogger logger,
        Guid instanceId,
        int limit);

    /// <summary>
    /// Logs when the Application-layer related-instance reader catches an exception at the
    /// repository boundary while reading the target instance.
    /// </summary>
    [LoggerMessage(
        EventId = 20435,
        Level = LogLevel.Error,
        Message = "Related instance read failed. Target: {TargetInstanceId}, Flow: {TargetFlow}")]
    public static partial void RelatedInstanceReadFailed(
        this ILogger logger,
        Exception exception,
        Guid targetInstanceId,
        string targetFlow);

    /// <summary>
    /// Logs when resolving a batch of related instances failed due to an infrastructure problem;
    /// the accessor throws after logging this. Unlike <see cref="RelatedInstanceResolutionFailed"/>,
    /// a batch can span several domains, so every distinct target domain is named rather than one.
    /// </summary>
    [LoggerMessage(
        EventId = 20436,
        Level = LogLevel.Error,
        Message = "Related instance batch resolution failed. Instance: {InstanceId}, Count: {Count}, TargetDomains: {TargetDomains}, Reason: {Reason}")]
    public static partial void RelatedInstanceBatchResolutionFailed(
        this ILogger logger,
        Guid instanceId,
        int count,
        string targetDomains,
        string reason);

    #endregion

    #region Caller Role Provider (2044x)

    /// <summary>
    /// Logs a successful role-set fetch from an external caller-role provider. Debug: this happens at
    /// most once per request scope, but on every authorized request.
    /// </summary>
    [LoggerMessage(
        EventId = 20440,
        Level = LogLevel.Debug,
        Message = "Caller roles resolved from provider. Provider={Provider}, RoleCount={RoleCount}, ElapsedMs={ElapsedMs}")]
    public static partial void CallerRolesResolvedFromProvider(
        this ILogger logger,
        string provider,
        int roleCount,
        long elapsedMs);

    /// <summary>
    /// Logs when the provider answered that the caller has no operation set at all. This is a valid
    /// answer, not a failure — but it denies every allowlist grant, so it is worth seeing.
    /// </summary>
    [LoggerMessage(
        EventId = 20441,
        Level = LogLevel.Warning,
        Message = "Caller role provider returned no operation set. Provider={Provider}, Subject={Subject}, Actor={Actor}, Position={Position}")]
    public static partial void CallerRoleProviderReturnedNoContent(
        this ILogger logger,
        string provider,
        string? subject,
        string? actor,
        string? position);

    /// <summary>
    /// Logs a failed provider call. The request is denied (fail-closed) after this is written, so this
    /// is the only record of why a caller lost access.
    /// </summary>
    [LoggerMessage(
        EventId = 20442,
        Level = LogLevel.Error,
        Message = "Caller role provider call failed. Provider={Provider}, StatusCode={StatusCode}, Reason={Reason}")]
    public static partial void CallerRoleProviderCallFailed(
        this ILogger logger,
        Exception? exception,
        string provider,
        int? statusCode,
        string reason);

    /// <summary>
    /// Logs when a surface was served the memoized role set instead of triggering a second provider
    /// call. Useful to confirm the one-call-per-request guarantee holds under concurrent fan-out.
    /// </summary>
    [LoggerMessage(
        EventId = 20443,
        Level = LogLevel.Debug,
        Message = "Caller roles served from request-scope memo. Provider={Provider}, RoleCount={RoleCount}")]
    public static partial void CallerRolesServedFromRequestScopeMemo(
        this ILogger logger,
        string provider,
        int roleCount);

    /// <summary>
    /// Logs when the long-poll ownership gate could not establish the caller's roles and therefore
    /// declined to arm the pause. The transition continues normally; nothing faults.
    /// </summary>
    [LoggerMessage(
        EventId = 20444,
        Level = LogLevel.Warning,
        Message = "Long-poll ownership undetermined: caller roles unresolved. InstanceId={InstanceId}, State={StateKey}")]
    public static partial void LongPollOwnershipUndeterminedRoles(
        this ILogger logger,
        Guid instanceId,
        string stateKey);

    #endregion

    #region Instance Query Filtering

    /// <summary>
    /// Logs when an instance-query parameter was rejected by the boundary validator and the query
    /// was never executed. One entry per validation error.
    /// </summary>
    [LoggerMessage(
        EventId = 20440,
        Level = LogLevel.Warning,
        Message = "Instance query rejected. Domain: {Domain}, Workflow: {Workflow}, Parameter: {Parameter}, Code: {ErrorCode}, Reason: {Reason}")]
    public static partial void InstanceQueryParameterRejected(
        this ILogger logger,
        string domain,
        string workflow,
        string parameter,
        string errorCode,
        string reason);

    /// <summary>
    /// Logs when a filter that passed boundary validation still failed while being compiled into
    /// SQL. This means the validator's whitelist and the SQL builder's whitelist have drifted —
    /// the caller sees an error either way, but the drift is a defect worth investigating.
    /// </summary>
    [LoggerMessage(
        EventId = 20441,
        Level = LogLevel.Error,
        Message = "Instance filter compilation failed after passing validation. Domain: {Domain}, Workflow: {Workflow}")]
    public static partial void InstanceFilterCompilationFailed(
        this ILogger logger,
        Exception exception,
        string domain,
        string workflow);

    /// <summary>
    /// Logs when a workflow task's authored filter was rejected. Unlike a client-supplied filter,
    /// this is a definition defect: the task fails so the error boundary can act on it, rather than
    /// loading every instance of the target workflow into instance data.
    /// </summary>
    [LoggerMessage(
        EventId = 20442,
        Level = LogLevel.Warning,
        Message = "Task filter rejected. TaskKey: {TaskKey}, TargetDomain: {TargetDomain}, TargetFlow: {TargetFlow}, Code: {ErrorCode}, Reason: {Reason}")]
    public static partial void InstanceTaskFilterRejected(
        this ILogger logger,
        string taskKey,
        string targetDomain,
        string targetFlow,
        string errorCode,
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

    /// <summary>
    /// Logs that the startup script-engine warmup finished: Roslyn assemblies are loaded, the
    /// compiler pipeline is JIT'd and the default reference set is materialized, so the first real
    /// mapping compile no longer pays that one-time cost.
    /// </summary>
    [LoggerMessage(
        EventId = 60015,
        Level = LogLevel.Information,
        Message = "Script engine warmup compiled the probe script in {DurationMs} ms")]
    public static partial void ScriptEngineWarmupCompleted(
        this ILogger logger,
        long durationMs);

    /// <summary>
    /// Logs that the startup script-engine warmup failed. Non-fatal by design: the first real
    /// compile simply pays the cold cost the warmup would have absorbed.
    /// </summary>
    [LoggerMessage(
        EventId = 60016,
        Level = LogLevel.Warning,
        Message = "Script engine warmup failed; the first real compile pays the cold cost")]
    public static partial void ScriptEngineWarmupFailed(
        this ILogger logger,
        Exception exception);

    #endregion

    #region Function Contract (800xx)

    /// <summary>
    /// Logs when a request is rejected because the function does not declare support for its HTTP verb.
    /// </summary>
    [LoggerMessage(
        EventId = 80001,
        Level = LogLevel.Warning,
        Message = "Function {FunctionKey} rejected HTTP {HttpMethod}; declared verbs: {AllowedVerbs}")]
    public static partial void FunctionVerbRejected(
        this ILogger logger,
        string functionKey,
        string httpMethod,
        string allowedVerbs);

    /// <summary>
    /// Logs when a request body fails validation against the function's declared input schema.
    /// </summary>
    [LoggerMessage(
        EventId = 80002,
        Level = LogLevel.Warning,
        Message = "Function {FunctionKey} input schema validation failed against {SchemaKey}")]
    public static partial void FunctionInputSchemaValidationFailed(
        this ILogger logger,
        string functionKey,
        string schemaKey);

    /// <summary>
    /// Logs when a rule on a function contract entry could not be evaluated. The entry is skipped and
    /// evaluation continues with the next one, mirroring state/transition view rule handling.
    /// </summary>
    [LoggerMessage(
        EventId = 80004,
        Level = LogLevel.Warning,
        Message = "Function {FunctionKey} contract rule evaluation failed for slot {Slot} entry {ReferenceKey}: {ErrorMessage}. Skipping entry.")]
    public static partial void FunctionContractRuleEvaluationFailed(
        this ILogger logger,
        string functionKey,
        string slot,
        string referenceKey,
        string errorMessage);

    /// <summary>
    /// Logs when a function reference declared on a workflow cannot be resolved while building the
    /// state response. The entry is omitted from the response's function list; polling continues.
    /// </summary>
    [LoggerMessage(
        EventId = 80005,
        Level = LogLevel.Warning,
        Message = "Workflow {WorkflowKey} declares function {FunctionKey} but its component could not be resolved: {ErrorMessage}. Omitting it from the state response.")]
    public static partial void WorkflowFunctionReferenceUnresolved(
        this ILogger logger,
        string workflowKey,
        string functionKey,
        string errorMessage);

    #endregion

    #region Component Cache

    /// <summary>
    /// Logs when a component's generation token is replaced, making every prior resolution entry for
    /// that component unreachable.
    /// </summary>
    [LoggerMessage(
        EventId = 70001,
        Level = LogLevel.Debug,
        Message = "Component cache generation bumped for {ComponentType} {Domain}/{Key} (token {Token})")]
    public static partial void ComponentCacheGenerationBumped(
        this ILogger logger,
        string componentType,
        string domain,
        string key,
        string token);

    /// <summary>
    /// Logs when writing a new generation token failed but the token was successfully removed instead.
    /// An absent token forces the next reader to bootstrap a fresh one, so invalidation still holds.
    /// </summary>
    [LoggerMessage(
        EventId = 70002,
        Level = LogLevel.Warning,
        Message = "Component cache generation write failed for {ComponentType} {Domain}/{Key}; removed the token instead, invalidation still applies")]
    public static partial void ComponentCacheGenerationBumpFellBackToRemove(
        this ILogger logger,
        Exception exception,
        string componentType,
        string domain,
        string key);

    /// <summary>
    /// Logs when a generation token could be neither written nor removed. This is the only condition
    /// that leaves stale resolution entries reachable, so it is an error rather than a warning.
    /// </summary>
    [LoggerMessage(
        EventId = 70003,
        Level = LogLevel.Error,
        Message = "Component cache generation could not be bumped or removed for {ComponentType} {Domain}/{Key}; previously cached version resolutions remain reachable until the generation TTL expires")]
    public static partial void ComponentCacheGenerationBumpFailed(
        this ILogger logger,
        Exception exception,
        string componentType,
        string domain,
        string key);

    /// <summary>
    /// Logs when a component's generation token was absent and a fresh one was created.
    /// </summary>
    [LoggerMessage(
        EventId = 70004,
        Level = LogLevel.Debug,
        Message = "Component cache generation bootstrapped for {ComponentType} {Domain}/{Key} (token {Token})")]
    public static partial void ComponentCacheGenerationBootstrapped(
        this ILogger logger,
        string componentType,
        string domain,
        string key,
        string token);

    /// <summary>
    /// Logs when a version request was resolved from the backend because no cached resolution existed.
    /// </summary>
    [LoggerMessage(
        EventId = 70010,
        Level = LogLevel.Debug,
        Message = "Component cache resolved {ComponentType} {Domain}/{Key}@{Requested} to {Resolved}")]
    public static partial void ComponentCacheResolvedFromBackend(
        this ILogger logger,
        string componentType,
        string domain,
        string key,
        string requested,
        string resolved);

    /// <summary>
    /// Logs when a version request matched no published version and a short-lived negative entry
    /// was cached to stop repeated backend loads.
    /// </summary>
    [LoggerMessage(
        EventId = 70011,
        Level = LogLevel.Debug,
        Message = "Component cache stored a negative entry for {ComponentType} {Domain}/{Key}@{Requested}")]
    public static partial void ComponentCacheNegativeStored(
        this ILogger logger,
        string componentType,
        string domain,
        string key,
        string requested);

    /// <summary>
    /// Logs when more than one stored version shares an artifact and package version, differing only in
    /// build metadata. Build metadata does not participate in ordering, so such versions are
    /// indistinguishable to version resolution and one is chosen deterministically.
    /// </summary>
    [LoggerMessage(
        EventId = 70012,
        Level = LogLevel.Warning,
        Message = "Component {ComponentType} {Domain}/{Key} has {Count} versions matching {CanonicalVersion} that differ only in build metadata; resolved to {Resolved}")]
    public static partial void ComponentCacheBuildMetadataAmbiguity(
        this ILogger logger,
        string componentType,
        string domain,
        string key,
        int count,
        string canonicalVersion,
        string resolved);

    /// <summary>
    /// Logs when a component cache read or write failed. Reads degrade to a backend load and writes
    /// are dropped, so neither is fatal.
    /// </summary>
    [LoggerMessage(
        EventId = 70020,
        Level = LogLevel.Warning,
        Message = "Component cache {Operation} failed for key {CacheKey}")]
    public static partial void ComponentCacheOperationFailed(
        this ILogger logger,
        Exception exception,
        string operation,
        string cacheKey);

    #endregion
}