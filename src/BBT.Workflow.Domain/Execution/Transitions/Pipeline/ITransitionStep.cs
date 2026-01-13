using BBT.Aether.Results;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Represents a single step in the transition execution pipeline.
/// Each step performs a specific operation during the transition lifecycle.
/// Uses Result pattern to provide exception-free error handling.
/// </summary>
public interface ITransitionStep
{
    /// <summary>
    /// Gets the execution order of this step in the pipeline.
    /// Steps are executed in ascending order of their Order value.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Gets the display name of this step for logging and telemetry.
    /// </summary>
    string Name => GetType().Name;

    /// <summary>
    /// Executes this step of the transition pipeline using Result pattern.
    /// Returns a Result containing the StepOutcome or an Error if the step fails.
    /// </summary>
    /// <param name="context">The transition execution context containing all necessary data.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A Result containing the StepOutcome or an error.</returns>
    Task<Result<StepOutcome>> ExecuteAsync(TransitionExecutionContext context, CancellationToken cancellationToken);
}