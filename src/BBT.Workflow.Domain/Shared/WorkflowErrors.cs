using BBT.Workflow.Definitions;
using BBT.Workflow.Shared;

namespace BBT.Workflow.Domain;

/// <summary>
/// Domain-specific error factory for workflow operations.
/// Provides strongly-typed error creation with workflow-specific error codes.
/// </summary>
public static class WorkflowErrors
{
    #region Instance Errors

    /// <summary>
    /// Instance already exists with the given key.
    /// </summary>
    public static Error InstanceAlreadyExists(string instanceKey)
        => Error.Conflict(
            WorkflowErrorCodes.ConflictWorkflow,
            $"An active instance with key \"{instanceKey}\" already exists",
            target: instanceKey);

    /// <summary>
    /// Instance was not found by ID or key.
    /// </summary>
    public static Error InstanceNotFound(string instanceIdentifier)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundInstanceData,
            $"Instance \"{instanceIdentifier}\" not found",
            target: instanceIdentifier);

    #endregion

    #region Workflow Definition Errors

    /// <summary>
    /// Workflow definition was not found.
    /// </summary>
    public static Error WorkflowNotFound(string workflowKey, string? version = null)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundWorkflow,
            $"Workflow \"{workflowKey}\" " + (version != null ? $"version \"{version}\" " : "") + "not found",
            target: workflowKey);

    /// <summary>
    /// Workflow state was not found.
    /// </summary>
    public static Error StateNotFound(string flow, string state)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundInitialState,
            $"No {state} state found for workflow \"{flow}\"",
            target: state);

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
    /// Transition was not found.
    /// </summary>
    public static Error TransitionNotFound(string transitionKey)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundTransition,
            $"Transition \"{transitionKey}\" not found",
            target: transitionKey);

    /// <summary>
    /// Transition rule evaluation failed.
    /// </summary>
    public static Error TransitionRuleFailed(string transitionKey, string reason)
        => Error.Validation(
            WorkflowErrorCodes.TransitionRuleFailed,
            $"Transition \"{transitionKey}\" rule evaluation failed: {reason}",
            target: transitionKey);

    /// <summary>
    /// Transition schema validation failed with detailed field-level errors.
    /// </summary>
    public static Error SchemaValidationFailed(
        string transitionKey,
        IReadOnlyCollection<System.ComponentModel.DataAnnotations.ValidationResult> validationErrors)
        => Error.Validation(
            WorkflowErrorCodes.ValidationErrors,
            $"Transition \"{transitionKey}\" schema validation failed",
            validationErrors,
            target: transitionKey);

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

    #endregion

    #region Configuration Errors

    /// <summary>
    /// SubFlow configuration is invalid or not found.
    /// </summary>
    public static Error ConfigInvalid(Guid instanceId, string state)
        => Error.Validation(
            WorkflowErrorCodes.ConfigInvalid,
            $"SubFlow configuration not found for state {state} on instance {instanceId}",
            target: instanceId.ToString());
    #endregion
 
    #region Instance Data Validation Errors
 
    /// <summary>
    /// Instance data schema validation failed with detailed field-level errors.
    /// </summary>
    public static Error InstanceDataSchemaValidationFailed(
        Guid instanceId,
        InstanceDataValidationMode validationMode,
        IReadOnlyCollection<System.ComponentModel.DataAnnotations.ValidationResult> validationErrors)
        => Error.Validation(
            WorkflowErrorCodes.InstanceDataSchemaValidationFailed,
            $"Instance data validation failed for instance {instanceId} in {validationMode} mode",
            validationErrors,
            target: instanceId.ToString());
    #endregion
}