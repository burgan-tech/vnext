using BBT.Workflow.Execution.ErrorHandling;

namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Represents the outcome of a pipeline step execution and controls pipeline flow.
/// </summary>
public sealed class StepOutcome
{
    /// <summary>
    /// Gets a value indicating whether the pipeline should stop completely.
    /// </summary>
    public bool StopPipeline { get; init; }
    
    /// <summary>
    /// Gets the order number to skip to in the pipeline execution.
    /// </summary>
    public int? SkipToOrder { get; init; }
    
    /// <summary>
    /// Gets the key of the next transition to redirect to in the same call.
    /// </summary>
    public string? NextTransitionKey { get; init; }
    
    /// <summary>
    /// Gets an action to mutate the pipeline directives.
    /// </summary>
    public Action<PipelineDirectives>? MutateDirectives { get; init; }

    /// <summary>
    /// Gets the error action result from error boundary processing.
    /// Used when an error was caught and handled by the error boundary system.
    /// </summary>
    public ErrorActionResult? ErrorActionResult { get; init; }

    /// <summary>
    /// Gets a value indicating whether the pipeline should stop for a scheduled retry.
    /// This is a graceful stop - the retry will be executed by a background job.
    /// </summary>
    public bool StopForRetry { get; init; }

    /// <summary>
    /// Creates an outcome that continues to the next pipeline step.
    /// </summary>
    /// <returns>A StepOutcome that allows pipeline continuation.</returns>
    public static StepOutcome Continue() => new();
    
    /// <summary>
    /// Creates an outcome that stops the pipeline execution completely.
    /// </summary>
    /// <returns>A StepOutcome that stops the pipeline.</returns>
    public static StepOutcome Stop() => new() { StopPipeline = true };
    
    /// <summary>
    /// Creates an outcome that skips to a specific order in the pipeline.
    /// </summary>
    /// <param name="order">The order number to skip to.</param>
    /// <returns>A StepOutcome that skips to the specified order.</returns>
    public static StepOutcome SkipTo(int order) => new() { SkipToOrder = order };
    
    /// <summary>
    /// Creates an outcome that mutates the pipeline directives.
    /// </summary>
    /// <param name="fx">The action to apply to the directives.</param>
    /// <returns>A StepOutcome with directive mutation.</returns>
    public static StepOutcome With(Action<PipelineDirectives> fx) => new() { MutateDirectives = fx };

    /// <summary>
    /// Creates an outcome that stops the pipeline for a scheduled retry.
    /// The retry will be executed by a background job after the configured delay.
    /// </summary>
    /// <returns>A StepOutcome that stops the pipeline for retry.</returns>
    public static StepOutcome RetryScheduled() => new() { StopForRetry = true };
}