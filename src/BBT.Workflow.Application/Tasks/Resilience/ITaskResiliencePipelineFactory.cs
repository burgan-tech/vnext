using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using Polly;

using ErrorBoundaryRetryPolicy = BBT.Workflow.Definitions.RetryPolicy;

namespace BBT.Workflow.Tasks.Resilience;

/// <summary>
/// Factory for creating Polly resilience pipelines for task execution.
/// Integrates with ErrorBoundary RetryPolicy for configurable retry behavior.
/// </summary>
public interface ITaskResiliencePipelineFactory
{
    /// <summary>
    /// Creates a Polly resilience pipeline for task execution.
    /// </summary>
    /// <param name="taskKey">The task key for logging/metrics.</param>
    /// <param name="errorBoundaryRetryPolicy">
    /// Optional retry policy from ErrorBoundary configuration.
    /// If null, default TaskRetryOptions will be used.
    /// </param>
    /// <returns>A resilience pipeline for task invocation results.</returns>
    ResiliencePipeline<Result<TaskInvocationResult>> CreatePipeline(
        string taskKey,
        ErrorBoundaryRetryPolicy? errorBoundaryRetryPolicy = null);

    /// <summary>
    /// Creates a Polly resilience pipeline for generic Result operations.
    /// </summary>
    /// <param name="taskKey">The task key for logging/metrics.</param>
    /// <param name="errorBoundaryRetryPolicy">
    /// Optional retry policy from ErrorBoundary configuration.
    /// </param>
    /// <returns>A resilience pipeline for Result operations.</returns>
    ResiliencePipeline<Result> CreateResultPipeline(
        string taskKey,
        ErrorBoundaryRetryPolicy? errorBoundaryRetryPolicy = null);

    /// <summary>
    /// Creates a Polly resilience pipeline for StandardTaskResponse.
    /// Handles retry based on both Result.IsSuccess and StandardTaskResponse.StatusCode.
    /// </summary>
    /// <param name="taskKey">The task key for logging/metrics.</param>
    /// <param name="errorBoundaryRetryPolicy">
    /// Optional retry policy from ErrorBoundary configuration.
    /// </param>
    /// <returns>A resilience pipeline for StandardTaskResponse operations.</returns>
    ResiliencePipeline<Result<StandardTaskResponse>> CreateStandardResponsePipeline(
        string taskKey,
        ErrorBoundaryRetryPolicy? errorBoundaryRetryPolicy = null);

    /// <summary>
    /// Creates a Polly resilience pipeline for StandardTaskResponse using the full ErrorHandlerRule.
    /// Only retries for error codes specified in the rule's ErrorCodes list.
    /// This ensures that other error codes (e.g., those configured for Abort) are not retried.
    /// </summary>
    /// <param name="taskKey">The task key for logging/metrics.</param>
    /// <param name="retryRule">
    /// The complete error handler rule containing ErrorCodes, ErrorTypes, and RetryPolicy.
    /// If null, no retry will be performed.
    /// </param>
    /// <returns>A resilience pipeline for StandardTaskResponse operations.</returns>
    ResiliencePipeline<Result<StandardTaskResponse>> CreateStandardResponsePipelineFromRule(
        string taskKey,
        ErrorHandlerRule? retryRule);
}
