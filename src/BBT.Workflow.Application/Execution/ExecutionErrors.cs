using BBT.Aether.Results;
using BBT.Workflow.Execution.Pipeline;

namespace BBT.Workflow.Execution;

/// <summary>
/// Centralized factory methods for execution-related domain errors.
/// Provides strongly-typed error creation to reduce string noise and improve code readability.
/// </summary>
/// </remarks>
public static class ExecutionErrors
{
    #region Instance Errors

    /// <summary>
    /// Creates an error when attempting to cancel an already completed instance.
    /// </summary>
    /// <param name="instanceId">The ID of the workflow instance.</param>
    /// <param name="currentStatus">The current status description of the instance.</param>
    public static Error InstanceAlreadyCompleted(Guid instanceId, string currentStatus)
        => Error.Validation(
            WorkflowErrorCodes.InvalidState,
            $"Cannot cancel instance {instanceId}: already in {currentStatus} state",
            target: instanceId.ToString());

    /// <summary>
    /// Creates an error when a workflow instance is not found during transition response build.
    /// </summary>
    /// <param name="instanceId">The ID of the workflow instance.</param>
    public static Error InstanceNotFoundForResponse(string instanceId)
        => Error.NotFound(
            WorkflowErrorCodes.NotFoundInstanceData,
            $"Workflow instance {instanceId} not found during transition response build.",
            target: instanceId);
    
    #endregion

    #region Transition Errors

    /// <summary>
    /// Creates an error when no automatic transition condition is satisfied.
    /// </summary>
    /// <param name="stateKey">The key of the state being evaluated.</param>
    public static Error NoAutoTransitionConditionSatisfied(string stateKey)
        => Error.Validation(
            WorkflowErrorCodes.AutoTransitionConditionNotMet,
            $"No automatic transition condition is satisfied for state '{stateKey}'. " +
            "At least one automatic transition must have a satisfied condition.");

    /// <summary>
    /// Creates an error when an automatic transition has no rule defined.
    /// </summary>
    /// <param name="transitionKey">The key of the transition without a rule.</param>
    public static Error AutoTransitionNoRuleDefined(string transitionKey)
        => Error.Validation(
            WorkflowErrorCodes.ConfigInvalid,
            $"Automatic transition '{transitionKey}' has no rule defined.");

    /// <summary>
    /// Creates an error when automatic transition rule evaluation fails.
    /// </summary>
    /// <param name="transitionKey">The key of the transition that failed.</param>
    /// <param name="errorMessage">The error message from the script execution.</param>
    /// <param name="detail">Additional detail (e.g., exception details).</param>
    public static Error TransitionRuleEvaluationFailed(string transitionKey, string errorMessage, string? detail = null)
        => Error.Failure(
            WorkflowErrorCodes.TransitionRuleFailed,
            $"Automatic transition rule evaluation failed for '{transitionKey}': {errorMessage}",
            detail: detail);

    /// <summary>
    /// Creates an error when transition mapping script execution fails.
    /// </summary>
    /// <param name="errorMessage">The error message from the script execution.</param>
    /// <param name="exceptionType">The type of exception that occurred.</param>
    public static Error TransitionMappingFailed(string errorMessage, string exceptionType)
        => Error.Failure(
            WorkflowErrorCodes.ExecutionStepFailed,
            $"Failed to execute transition mapping: {errorMessage}",
            detail: exceptionType);
    
    #endregion

    #region Instance Key Errors

    /// <summary>
    /// Creates an error when attempting to set a key that already exists on another active instance.
    /// </summary>
    /// <param name="key">The duplicate key value.</param>
    /// <param name="instanceId">The current instance ID attempting to use the key.</param>
    public static Error DuplicateInstanceKey(string key, Guid instanceId)
        => Error.Conflict(
            WorkflowErrorCodes.DuplicateInstanceKey,
            $"An active instance with key '{key}' already exists. Instance {instanceId} cannot use this key.",
            target: key);

    #endregion

    #region Resource Lock Errors

    /// <summary>
    /// Creates an error when a resource lock cannot be acquired because it is held by another instance.
    /// </summary>
    /// <param name="resourceKey">The lock key that caused the conflict.</param>
    public static Error ResourceLockConflict(string resourceKey)
        => Error.Conflict(
            WorkflowErrorCodes.ResourceLockConflict,
            $"Resource lock conflict for key '{resourceKey}'. The resource is already locked by another instance.",
            target: resourceKey);

    /// <summary>
    /// Creates an error when the keyExpression script returns a null or empty lock key.
    /// </summary>
    /// <param name="transitionKey">The transition whose keyExpression produced an empty key.</param>
    public static Error ResourceLockKeyEmpty(string transitionKey)
        => Error.Validation(
            WorkflowErrorCodes.ResourceLockKeyEmpty,
            $"resourceLock.keyExpression for transition '{transitionKey}' returned a null or empty key.");

    /// <summary>
    /// Creates an error when keyExpression compilation or execution fails.
    /// </summary>
    /// <param name="transitionKey">The transition whose keyExpression failed.</param>
    /// <param name="errorMessage">Details from the underlying exception.</param>
    public static Error ResourceLockKeyResolutionFailed(string transitionKey, string errorMessage)
        => Error.Failure(
            WorkflowErrorCodes.ResourceLockKeyResolutionFailed,
            $"Failed to resolve resource lock key for transition '{transitionKey}': {errorMessage}");

    /// <summary>
    /// Creates an error when an unknown resource lock action is encountered.
    /// </summary>
    /// <param name="action">The unrecognized action value.</param>
    public static Error ResourceLockInvalidAction(string action)
        => Error.Validation(
            WorkflowErrorCodes.ResourceLockInvalidAction,
            $"Unknown resource lock action: {action}");

    /// <summary>
    /// Creates an error when a resource lock release fails because the lock is not held by this owner.
    /// </summary>
    /// <param name="resourceKey">The lock key that could not be released.</param>
    public static Error ResourceLockReleaseFailed(string resourceKey)
        => Error.Failure(
            WorkflowErrorCodes.ResourceLockReleaseFailed,
            $"Failed to release resource lock for key '{resourceKey}'. Lock not held by this owner.",
            detail: resourceKey);

    #endregion

    #region Task / Pipeline Errors

    /// <summary>
    /// Creates an error when one or more tasks failed at business level without an ErrorBoundary,
    /// and the workflow did not handle the failure via automatic transitions (or epilogue was skipped).
    /// </summary>
    /// <param name="transitionKey">The transition key being executed.</param>
    /// <param name="stateKey">The state key where handling was expected (usually target state).</param>
    /// <param name="failures">The non-blocking failures collected from task steps.</param>
    /// <param name="reason">Optional reason for why handling didn't occur (e.g., EpilogueSkipped, NoAutoTransitions, NoAutoTransitionWinner).</param>
    public static Error UnhandledNonBlockingTaskFailures(
        string transitionKey,
        string stateKey,
        IReadOnlyList<NonBlockingTaskFailure> failures,
        string? reason = null)
    {
        var failedKeys = failures
            .SelectMany(f => f.FailedTaskKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var firstMessage = failures
            .Select(f => f.FirstErrorMessage)
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

        var reasonPart = string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;
        var keysPart = failedKeys.Count == 0 ? "Unknown" : string.Join(", ", failedKeys);

        var message =
            $"Unhandled task business failures without ErrorBoundary for transition '{transitionKey}' " +
            $"(state: '{stateKey}', reason: '{reasonPart}'). Failed task keys: [{keysPart}].";

        if (!string.IsNullOrWhiteSpace(firstMessage))
        {
            message = $"{message} First error: {firstMessage}";
        }

        return Error.Failure(
            WorkflowErrorCodes.TaskExecutionFailed,
            message,
            detail: stateKey);
    }

    #endregion
    
}

