using BBT.Aether.Results;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Service for invoking task executors with cross-cutting concerns like error boundary and retry.
/// Unlike decorator pattern, this service does not wrap executors - it invokes them through a pipeline.
/// </summary>
/// <remarks>
/// This approach avoids per-invocation object allocation by:
/// - Being registered as a scoped service (single instance per request)
/// - Accepting the executor as a method parameter instead of constructor
/// - Managing Polly pipelines and error policies centrally
/// </remarks>
public interface ITaskExecutorInvoker
{
    /// <summary>
    /// Invokes the given executor with error boundary and retry capabilities.
    /// </summary>
    /// <param name="executor">The task executor to invoke.</param>
    /// <param name="context">The execution context containing task, mapping, and script context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result containing the standard task response.</returns>
    Task<Result<StandardTaskResponse>> InvokeAsync(
        ITaskExecutor executor,
        TaskExecutorContext context,
        CancellationToken cancellationToken = default);
}

