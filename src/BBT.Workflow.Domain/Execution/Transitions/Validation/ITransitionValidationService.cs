using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Execution.Validation;

/// <summary>
/// Interface for transition validation operations within the execution pipeline using Result Pattern.
/// This service handles transition validation, rule execution, schema validation, and policy checks without throwing exceptions.
/// </summary>
public interface ITransitionValidationService
{
    /// <summary>
    /// Validates a transition execution context before pipeline execution using Result Pattern.
    /// Returns Result.Ok() if all validations pass, or Result.Fail() with detailed error information on failure.
    /// </summary>
    /// <param name="context">The transition execution context containing all necessary data</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Result indicating validation success or failure with detailed error information</returns>
    /// <remarks>
    /// This method performs validation steps using Result pattern:
    /// 1. Checks if the instance can execute the transition based on policies (returns Result with rule error details)
    /// 2. Validates input data against the transition's schema if present (returns Result with field-level validation errors)
    /// All validations use Result pattern and provide detailed error information for client consumption.
    /// </remarks>
    Task<Result> ValidateAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a start transition for a new instance before persistence using Result Pattern.
    /// This method is used during instance creation to validate the start transition before the instance is persisted.
    /// Returns Result.Ok() if all validations pass, or Result.Fail() with detailed error information on failure.
    /// </summary>
    /// <param name="workflow">The workflow definition</param>
    /// <param name="instance">The instance that will be persisted (not yet persisted)</param>
    /// <param name="transition">The start transition to validate</param>
    /// <param name="data">The input data/attributes for the transition</param>
    /// <param name="runtimeInfoProvider">Runtime information provider for domain and version</param>
    /// <param name="headers">Optional request headers</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation</param>
    /// <returns>Result indicating validation success or failure with detailed error information</returns>
    /// <remarks>
    /// This method performs the same validation as ValidateAsync but is specifically designed for start transitions
    /// where the instance has not yet been persisted. It constructs a TransitionExecutionContext internally and
    /// reuses the existing validation logic to ensure consistency.
    /// </remarks>
    Task<Result> ValidateStartTransitionAsync(
        Definitions.Workflow workflow,
        Instance instance,
        Transition transition,
        object? data,
        IRuntimeInfoProvider runtimeInfoProvider,
        IReadOnlyDictionary<string, string?>? headers = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates trigger type rules and execution context.
    /// Checks actor permissions, chain depth, and scheduled transition behavior.
    /// </summary>
    /// <param name="context">The transition execution context</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result indicating validation success or failure</returns>
    /// <remarks>
    /// This method validates:
    /// - Actor permissions based on trigger type (User for Manual, System for others)
    /// - Chain depth limits for Automatic transitions
    /// - Scheduled transition skip logic based on IsReentry flag
    /// Returns Result.Ok() if valid, or Result.Fail() with specific error details
    /// </remarks>
    Task<Result> ValidateTriggerTypeAsync(
        TransitionExecutionContext context,
        CancellationToken cancellationToken = default);
}
