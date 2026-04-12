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

    /// <summary>
    /// Invalid actor for manual transition (requires User actor).
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="actor">The current actor that is invalid.</param>
    public static Error InvalidActorForManualTransition(Guid instanceId, ExecutionActor actor)
        => Error.Validation(
            WorkflowErrorCodes.UnauthorizedTransition,
            $"Manual transitions require User actor. Current actor: {actor}",
            target: instanceId.ToString());

    /// <summary>
    /// Invalid actor for automatic transition (requires System actor).
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="actor">The current actor that is invalid.</param>
    public static Error InvalidActorForAutomaticTransition(Guid instanceId, ExecutionActor actor)
        => Error.Validation(
            WorkflowErrorCodes.UnauthorizedTransition,
            $"Automatic transitions require System actor. Current actor: {actor}",
            target: instanceId.ToString());

    /// <summary>
    /// Invalid actor for scheduled transition (requires System actor).
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="actor">The current actor that is invalid.</param>
    public static Error InvalidActorForScheduledTransition(Guid instanceId, ExecutionActor actor)
        => Error.Validation(
            WorkflowErrorCodes.UnauthorizedTransition,
            $"Scheduled transitions require System actor. Current actor: {actor}",
            target: instanceId.ToString());

    /// <summary>
    /// Invalid actor for event transition (requires User actor).
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="actor">The current actor that is invalid.</param>
    public static Error InvalidActorForEventTransition(Guid instanceId, ExecutionActor actor)
        => Error.Validation(
            WorkflowErrorCodes.UnauthorizedTransition,
            $"Event transitions require User actor. Current actor: {actor}",
            target: instanceId.ToString());

    /// <summary>
    /// Transition is not available in the current state.
    /// </summary>
    /// <param name="transitionKey">The transition key that is not available.</param>
    /// <param name="currentState">The current state key.</param>
    public static Error TransitionNotAvailableInCurrentState(string transitionKey, string currentState)
        => Error.Validation(
            WorkflowErrorCodes.TransitionNotAvailableInCurrentState,
            $"Transition '{transitionKey}' is not available in current state '{currentState}'",
            target: transitionKey);

    /// <summary>
    /// Shared transition is not available in the current state.
    /// </summary>
    /// <param name="transitionKey">The shared transition key.</param>
    /// <param name="currentState">The current state key.</param>
    public static Error SharedTransitionNotAvailableInState(string transitionKey, string currentState)
        => Error.Validation(
            WorkflowErrorCodes.SharedTransitionNotAvailableInState,
            $"Shared transition '{transitionKey}' is not available in state '{currentState}'. Check AvailableIn configuration.",
            target: transitionKey);

    /// <summary>
    /// When the instance has an active SubFlow, a shared transition's target must be $self so the state does not change.
    /// </summary>
    /// <param name="transitionKey">The shared transition key.</param>
    /// <param name="currentTarget">The transition's target value (must be $self).</param>
    public static Error SharedTransitionTargetMustBeSelfWhenInSubFlow(string transitionKey, string currentTarget)
        => Error.Validation(
            WorkflowErrorCodes.SharedTransitionTargetMustBeSelfWhenInSubFlow,
            $"Shared transition '{transitionKey}' cannot be executed while in active SubFlow with target '{currentTarget}'. Target must be '$self'.",
            target: transitionKey);

    /// <summary>
    /// StartTransition can only be executed from Initial state.
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="currentStateKey">The current state key.</param>
    /// <param name="currentStateType">The current state type.</param>
    public static Error StartTransitionNotFromInitialState(Guid instanceId, string currentStateKey, StateType currentStateType)
        => Error.Validation(
            WorkflowErrorCodes.StartTransitionNotFromInitialState,
            $"Start transition can only be executed from Initial state. Instance {instanceId} is currently in state '{currentStateKey}' (Type: {currentStateType})",
            target: instanceId.ToString());

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

    #region Timeout Errors

    /// <summary>
    /// Workflow timeout configuration is missing.
    /// </summary>
    /// <param name="workflowKey">The key of the workflow with missing timeout config.</param>
    public static Error TimeoutConfigMissing(string workflowKey)
        => Error.Validation(
            WorkflowErrorCodes.TimeoutConfigMissing,
            $"Timeout configuration is missing for workflow '{workflowKey}'",
            target: workflowKey);

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

    /// <summary>
    /// Failed to acquire distributed lock for schema migration.
    /// Another instance is already migrating this schema.
    /// </summary>
    /// <param name="schemaName">The name of the schema that could not be locked.</param>
    public static Error SchemaMigrationLockFailed(string schemaName)
        => Error.Conflict(
            WorkflowErrorCodes.ConflictWorkflow,
            $"Schema '{schemaName}' is already being migrated by another instance",
            target: schemaName);

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

    /// <summary>
    /// A background job for this transition is already active.
    /// Returned when sync=false is requested but an active job with the same
    /// instance and transition key already exists in the queue.
    /// </summary>
    /// <param name="instanceId">The instance being processed.</param>
    /// <param name="transitionKey">The transition key that is already queued.</param>
    public static Error TransitionJobAlreadyActive(Guid instanceId, string transitionKey)
        => Error.Conflict(
            WorkflowErrorCodes.TransitionLocked,
            $"Transition '{transitionKey}' is already being processed for instance",
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

    #region Extension Errors

    /// <summary>
    /// Extension execution failed during instance data enrichment.
    /// </summary>
    /// <param name="extensionKey">The key of the extension that failed.</param>
    /// <param name="message">The error message from the failed execution.</param>
    public static Error ExtensionExecutionFailed(string extensionKey, string message)
        => Error.Validation(
            WorkflowErrorCodes.ExtensionExecutionFailed,
            $"Extension '{extensionKey}' execution failed: {message}",
            target: extensionKey);

    #endregion

    #region Function Errors

    /// <summary>
    /// Function is not declared in the workflow's Functions list.
    /// Used when executing a non-system function that is not referenced by the workflow.
    /// </summary>
    /// <param name="functionKey">The key of the function that was not found in the workflow.</param>
    /// <param name="workflowKey">The key of the workflow.</param>
    public static Error FunctionNotInWorkflow(string functionKey, string workflowKey)
        => Error.Validation(
            WorkflowErrorCodes.FunctionNotInWorkflow,
            $"Function '{functionKey}' is not defined for workflow '{workflowKey}'",
            target: functionKey);

    #endregion

    #region Authorization Errors

    /// <summary>
    /// Authorize requires exactly one of transitionKey, functionKey, or queryRoles (instance only).
    /// </summary>
    public static Error AuthorizeRequiresExactlyOneTarget()
        => Error.Validation(
            WorkflowErrorCodes.AuthorizeRequiresExactlyOneTarget,
            "Authorize requires exactly one of transitionKey, functionKey, or queryRoles (instance only)",
            target: "authorize");

    /// <summary>
    /// Query roles check is only valid for instance-level authorize.
    /// </summary>
    public static Error AuthorizeQueryRolesRequiresInstance()
        => Error.Validation(
            WorkflowErrorCodes.AuthorizeQueryRolesRequiresInstance,
            "Query roles check is only valid for instance-level authorize",
            target: "authorize");

    #endregion

    #region Discovery Errors

    /// <summary>
    /// Domain endpoint was not found in service discovery cache or registry.
    /// </summary>
    public static Error DomainEndpointNotFound(string domain)
        => Error.NotFound(
            WorkflowErrorCodes.DomainEndpointNotFound,
            $"Service discovery endpoint not found for domain '{domain}'",
            target: domain);

    /// <summary>
    /// Domain discovery operation failed.
    /// </summary>
    public static Error DomainDiscoveryFailed(string domain, string reason)
        => Error.Failure(
            WorkflowErrorCodes.DomainDiscoveryFailed,
            $"Failed to discover endpoint for domain '{domain}': {reason}");

    #endregion
}

