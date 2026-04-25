using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Logging;

/// <summary>
/// Centralized EventId definitions for workflow logging.
/// EventIds follow a structured numbering scheme:
/// - 10xxx: Execution layer
/// - 20xxx: Orchestration layer  
/// - 40xxx: Application layer
/// 
/// Within each range:
/// - xx01-xx39: Information level
/// - xx40-xx69: Warning level
/// - xx70-xx99: Error level
/// </summary>
public static class WorkflowEventIds
{
    #region Execution Layer (10xxx)

    // Information (10001-10039)
    public static readonly EventId StateChanged = new(10003, nameof(StateChanged));
    public static readonly EventId TransitionEnqueued = new(10004, nameof(TransitionEnqueued));
    public static readonly EventId CancelTransitionDetected = new(10005, nameof(CancelTransitionDetected));
    public static readonly EventId CancelSkipToFinish = new(10006, nameof(CancelSkipToFinish));
    public static readonly EventId InstanceCanceling = new(10007, nameof(InstanceCanceling));
    public static readonly EventId InstanceCompleting = new(10008, nameof(InstanceCompleting));
    public static readonly EventId AutoTransitionSelected = new(10009, nameof(AutoTransitionSelected));
    public static readonly EventId TransitionTimerSkipped = new(10010, nameof(TransitionTimerSkipped));
    public static readonly EventId UpdateDataTransitionDetected = new(10011, nameof(UpdateDataTransitionDetected));
    public static readonly EventId UpdateDataSkipToFinish = new(10012, nameof(UpdateDataSkipToFinish));
    public static readonly EventId ExitTransitionDetected = new(10013, nameof(ExitTransitionDetected));
    public static readonly EventId ExitSkipToFinish = new(10014, nameof(ExitSkipToFinish));

    // Warning (10040-10069)
    public static readonly EventId TransitionRuleFailed = new(10040, nameof(TransitionRuleFailed));
    public static readonly EventId AutoTransitionNoRule = new(10044, nameof(AutoTransitionNoRule));
    public static readonly EventId MaxAutoHopsExceeded = new(10045, nameof(MaxAutoHopsExceeded));
    public static readonly EventId TransitionEnqueueFailed = new(10046, nameof(TransitionEnqueueFailed));
    public static readonly EventId CancelInstanceAlreadyCompleted = new(10047, nameof(CancelInstanceAlreadyCompleted));
    public static readonly EventId TargetStateNull = new(10048, nameof(TargetStateNull));
    public static readonly EventId AutoTransitionConditionNotSatisfied = new(10049, nameof(AutoTransitionConditionNotSatisfied));
    public static readonly EventId UpdateDataInstanceAlreadyCompleted = new(10054, nameof(UpdateDataInstanceAlreadyCompleted));
    public static readonly EventId ExitInstanceAlreadyCompleted = new(10055, nameof(ExitInstanceAlreadyCompleted));

    // Error (10070-10099)
    public static readonly EventId TaskExecutionFailed = new(10071, nameof(TaskExecutionFailed));
    public static readonly EventId SubFlowConfigInvalid = new(10074, nameof(SubFlowConfigInvalid));
    public static readonly EventId DynamicExpressoConditionInvalidEncoding = new(10076, nameof(DynamicExpressoConditionInvalidEncoding));
    public static readonly EventId DynamicExpressoConditionEvaluationFailed = new(10077, nameof(DynamicExpressoConditionEvaluationFailed));

    #endregion

    #region Orchestration Layer (20xxx)

    // Information (20001-20039)
    public static readonly EventId EventReceived = new(20007, nameof(EventReceived));

    // Warning (20040-20069)
    public static readonly EventId InstanceNotFound = new(20040, nameof(InstanceNotFound));

    #endregion

    #region Application Layer (40xxx)

    // Information (40001-40039)
    public static readonly EventId SubFlowEventReceived = new(40011, nameof(SubFlowEventReceived));
    public static readonly EventId SubFlowCorrelationCompleted = new(40012, nameof(SubFlowCorrelationCompleted));
    public static readonly EventId SubFlowParentContinuationStarted = new(40013, nameof(SubFlowParentContinuationStarted));
    public static readonly EventId SubFlowOutputMappingStarted = new(40014, nameof(SubFlowOutputMappingStarted));
    public static readonly EventId SubFlowPipelineResumed = new(40015, nameof(SubFlowPipelineResumed));
    public static readonly EventId WorkflowTimeoutScheduled = new(40016, nameof(WorkflowTimeoutScheduled));
    public static readonly EventId JobCompleted = new(40017, nameof(JobCompleted));
    public static readonly EventId InstanceCanceledEventReceived = new(40018, nameof(InstanceCanceledEventReceived));
    public static readonly EventId InstanceCanceledJobsProcessed = new(40019, nameof(InstanceCanceledJobsProcessed));
    public static readonly EventId ChildSubflowCancelRequestReceived = new(40020, nameof(ChildSubflowCancelRequestReceived));
    public static readonly EventId ChildSubflowCancelSucceeded = new(40021, nameof(ChildSubflowCancelSucceeded));
    public static readonly EventId SubFlowStateChangeReceived = new(40027, nameof(SubFlowStateChangeReceived));
    public static readonly EventId SubFlowStateChangeApplied = new(40028, nameof(SubFlowStateChangeApplied));
    public static readonly EventId SubFlowStateChangedEventReceived = new(40029, nameof(SubFlowStateChangedEventReceived));
    public static readonly EventId SubFlowStateUpdateFailed = new(40079, nameof(SubFlowStateUpdateFailed));

    // Warning (40040-40069)
    public static readonly EventId SubFlowCorrelationNotFound = new(40043, nameof(SubFlowCorrelationNotFound));
    public static readonly EventId InstanceLockFailed = new(40044, nameof(InstanceLockFailed));
    public static readonly EventId StartTransitionValidationFailed = new(40045, nameof(StartTransitionValidationFailed));
    public static readonly EventId WorkflowNotFoundWarning = new(40046, nameof(WorkflowNotFoundWarning));
    public static readonly EventId TimeoutConfigMissing = new(40047, nameof(TimeoutConfigMissing));
    public static readonly EventId JobCancelled = new(40048, nameof(JobCancelled));
    public static readonly EventId SubFlowTransitionsQueryFailed = new(40049, nameof(SubFlowTransitionsQueryFailed));
    public static readonly EventId ChildSubflowCancelFailed = new(40050, nameof(ChildSubflowCancelFailed));
    public static readonly EventId TransitionValidationFailed = new(40051, nameof(TransitionValidationFailed));

    // Error (40070-40099)
    public static readonly EventId SubFlowCompletionFailed = new(40073, nameof(SubFlowCompletionFailed));
    public static readonly EventId WorkflowTimeoutSchedulingFailed = new(40074, nameof(WorkflowTimeoutSchedulingFailed));
    public static readonly EventId JobFailed = new(40075, nameof(JobFailed));
    public static readonly EventId InstanceDeserializationFailed = new(40076, nameof(InstanceDeserializationFailed));
    public static readonly EventId InstanceCanceledProcessingFailed = new(40077, nameof(InstanceCanceledProcessingFailed));
    public static readonly EventId ChildSubflowCancelError = new(40078, nameof(ChildSubflowCancelError));

    #endregion
}
