using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Logging;

/// <summary>
/// Domain-specific error factory for workflow operations.
/// Provides strongly-typed error creation with workflow-specific error codes.
/// </summary>
public static class WorkflowErrors
{
    #region Instance Errors
    
    /// <summary>
    /// Active instance already exists with the given key (strict idempotency check).
    /// Used for service-to-service calls to prevent false positive correlations.
    /// </summary>
    /// <param name="instanceId">The ID of the existing active instance.</param>
    /// <param name="key">The key of the existing active instance (optional).</param>
    public static Error ActiveInstanceAlreadyExists(Guid instanceId, string? key)
        => Error.Conflict(
            WorkflowErrorCodes.ActiveInstanceAlreadyExists,
            $"An active instance already exists with {(key != null ? $"key '{key}'" : $"id '{instanceId}'")}",
            target: key ?? instanceId.ToString());

    /// <summary>
    /// Instance was not found by ID or key.
    /// </summary>
    public static Error InstanceNotFound(string instanceIdentifier)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundInstanceData,
            $"Instance \"{instanceIdentifier}\" not found",
            target: instanceIdentifier);
    
    /// <summary>
    /// Creates an error when instance data for a specific version is not found.
    /// </summary>
    /// <param name="key">The key of the instance.</param>
    /// <param name="version">The version that was not found.</param>
    public static Error InstanceDataNotFound(string key, string version)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundInstanceData,
            $"Instance data not found for key {key} and version {version}",
            target: $"{key}@{version}");

    #endregion

    #region Workflow Definition Errors

    /// <summary>
    /// Creates an error when a workflow version already exists (conflict).
    /// </summary>
    public static Error WorkflowVersionConflict()
        => Error.Conflict(
            WorkflowErrorCodes.ConflictWorkflow,
            "A record with the same version already exists.");

    /// <summary>
    /// Workflow state was not found.
    /// </summary>
    /// <param name="stateKey">The key of the state.</param>
    /// <param name="workflowKey">The key of the workflow.</param>
    public static Error StateNotFound(string stateKey, string workflowKey)
        => Error.NotFound(
            WorkflowErrorCodes.RuntimeSchemaInvalidState,
            $"State \"{stateKey}\" not found in workflow \"{workflowKey}\"",
            target: stateKey);

    /// <summary>
    /// Workflow state is invalid for the operation.
    /// </summary>
    public static Error InvalidState(string transition, string? fromState = "N/A", string? currentState = "N/A")
        => Error.Validation(
            WorkflowErrorCodes.InvalidState,
            $"Transition \"{transition}\" is not valid for the current state. Expected state: {fromState}, Current state: {currentState}",
            target: transition);

    #endregion

    #region Transition Errors

    /// <summary>
    /// Transition is not authorized for the execution context.
    /// </summary>
    public static Error TransitionUnauthorized(string transitionKey, TriggerType triggerType,
        ExecutionActor executionActor)
        => Error.Forbidden(
            WorkflowErrorCodes.UnauthorizedTransition,
            $"Transition '{transitionKey}' with trigger type '{triggerType}' cannot be executed by '{executionActor}' context");

    /// <summary>
    /// No automatic transition succeeded.
    /// </summary>
    public static Error AutoTransitionFailed(Guid instanceId, string workflow)
        => Error.Validation(
            WorkflowErrorCodes.AutoTransitionFailed,
            $"No automatic transition succeeded for InstanceId={instanceId} in Workflow={workflow}",
            target: instanceId.ToString());

    /// <summary>
    /// Automatic transition condition was not met.
    /// This is not an error in multi-auto-transition scenarios, just means this specific transition cannot proceed.
    /// </summary>
    public static Error AutoTransitionConditionNotMet(string transitionKey, string? reason = null)
        => Error.Validation(
            WorkflowErrorCodes.AutoTransitionConditionNotMet,
            $"Auto-transition \"{transitionKey}\" condition not met" + (reason != null ? $": {reason}" : ""),
            target: transitionKey);

    /// <summary>
    /// Transition chain depth exceeded the maximum allowed limit.
    /// This prevents infinite loops in automatic transition chains.
    /// </summary>
    /// <param name="currentDepth">The current chain depth.</param>
    /// <param name="maxDepth">The maximum allowed chain depth.</param>
    /// <param name="transitionKey">The transition key that exceeded the limit.</param>
    public static Error TransitionChainDepthExceeded(int currentDepth, int maxDepth, string? transitionKey = null)
        => Error.Validation(
            WorkflowErrorCodes.TransitionChainDepthExceeded,
            $"Transition chain depth limit exceeded ({currentDepth}/{maxDepth})" +
            (string.IsNullOrEmpty(transitionKey) ? "" : $" for transition '{transitionKey}'"),
            target: transitionKey);

    #endregion

    #region Task Errors

    /// <summary>
    /// Task headers conversion to dictionary failed.
    /// </summary>
    public static Error TaskHeadersConversionFailed(string taskKey, string reason)
        => Error.Validation(
            WorkflowErrorCodes.TaskHeadersConversionFailed,
            $"Failed to convert headers to dictionary for task '{taskKey}': {reason}",
            target: taskKey);

    #endregion

    #region Configuration Errors

    /// <summary>
    /// Configuration is invalid or not found.
    /// </summary>
    public static Error ConfigInvalid(Guid instanceId, string state)
        => Error.Validation(
            WorkflowErrorCodes.ConfigInvalid,
            $"Configuration not found for state {state} on instance {instanceId}",
            target: instanceId.ToString());

    #endregion
    
    #region Schema Errors

    /// <summary>
    /// Creates an error when schema is not configured in runtime options.
    /// </summary>
    /// <param name="schemaKey">The schema key that is not configured.</param>
    public static Error SchemaNotConfigured(string schemaKey)
        => Error.Validation(
            WorkflowErrorCodes.InvalidSchema,
            $"Schema '{schemaKey}' is not configured in runtime options",
            target: schemaKey);

    #endregion

    #region Domain Errors

    /// <summary>
    /// Domain validation failed.
    /// </summary>
    /// <param name="domain">The domain name that failed validation.</param>
    /// <param name="reason">The reason for the validation failure.</param>
    public static Error DomainValidationFailed(string domain, string reason)
        => Error.Validation(
            WorkflowErrorCodes.NotFoundDomain,
            $"Domain validation failed: {reason}",
            target: domain);

    #endregion

    #region Lock Errors

    /// <summary>
    /// Failed to acquire distributed lock for instance.
    /// </summary>
    /// <param name="instanceId">The ID of the instance that could not be locked.</param>
    public static Error InstanceLockConflict(Guid instanceId)
        => Error.Conflict(
            WorkflowErrorCodes.ConflictWorkflow,
            "Failed to acquire lock for instance",
            target: instanceId.ToString());

    #endregion
    
    #region Cancellation Errors

    /// <summary>
    /// Instance cancellation processing failed.
    /// </summary>
    /// <param name="instanceId">The ID of the instance that failed cancellation.</param>
    /// <param name="reason">The reason for the failure.</param>
    public static Error InstanceCancellationFailed(Guid instanceId, string reason)
        => Error.Failure(
            WorkflowErrorCodes.InstanceCancellationFailed,
            $"Failed to process instance cancellation for {instanceId}: {reason}");

    /// <summary>
    /// Child subflow cancellation failed.
    /// </summary>
    /// <param name="instanceId">The ID of the child subflow that failed cancellation.</param>
    /// <param name="reason">The reason for the failure.</param>
    public static Error ChildSubflowCancellationFailed(Guid instanceId, string reason)
        => Error.Failure(
            WorkflowErrorCodes.ChildSubflowCancellationFailed,
            $"Failed to cancel child subflow {instanceId}: {reason}");

    #endregion

    #region SubFlow Errors

    /// <summary>
    /// SubFlow start operation failed.
    /// </summary>
    /// <param name="subFlowKey">The key of the SubFlow that failed to start.</param>
    /// <param name="reason">The reason for the failure.</param>
    public static Error SubFlowStartFailed(string subFlowKey, string reason)
        => Error.Failure(
            WorkflowErrorCodes.SubflowStartFailed,
            $"Failed to start SubFlow '{subFlowKey}': {reason}",
            detail: subFlowKey);

    /// <summary>
    /// SubFlow input mapping failed.
    /// </summary>
    /// <param name="subFlowKey">The key of the SubFlow.</param>
    /// <param name="reason">The reason for the mapping failure.</param>
    public static Error SubFlowInputMappingFailed(string subFlowKey, string reason)
        => Error.Failure(
            WorkflowErrorCodes.SubflowStartFailed,
            $"SubFlow '{subFlowKey}' input mapping failed: {reason}",
            detail: subFlowKey);

    /// <summary>
    /// Correlation not found for SubFlow start.
    /// </summary>
    /// <param name="correlationId">The correlation ID that was not found.</param>
    /// <param name="instanceId">The instance ID.</param>
    public static Error SubFlowCorrelationNotFound(Guid correlationId, Guid instanceId)
        => Error.NotFound(
            WorkflowErrorCodes.ConfigInvalid,
            $"Correlation '{correlationId}' not found for instance '{instanceId}'",
            target: correlationId.ToString());

    /// <summary>
    /// Target state not found or has no SubFlow configuration.
    /// </summary>
    /// <param name="stateKey">The state key that was not found.</param>
    /// <param name="instanceId">The instance ID.</param>
    public static Error SubFlowTargetStateNotFound(string stateKey, Guid instanceId)
        => Error.NotFound(
            WorkflowErrorCodes.ConfigInvalid,
            $"Target state '{stateKey}' not found or has no SubFlow configuration for instance '{instanceId}'",
            target: stateKey);

    #endregion
}

