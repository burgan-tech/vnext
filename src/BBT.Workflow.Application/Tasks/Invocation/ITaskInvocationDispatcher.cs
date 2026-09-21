using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Executes a prepared task binding on whichever side the router selects. This is the ONLY place
/// the local-versus-remote decision is acted on: five task executors and the function response
/// cache all dispatch through here, so the decision cannot be spelled six slightly different ways.
/// </summary>
public interface ITaskInvocationDispatcher
{
    /// <summary>
    /// Runs the envelope locally or remotely per <see cref="ITaskInvocationRouter"/>.
    /// A transport failure comes back as a successful <see cref="Result{T}"/> carrying a failed
    /// <see cref="TaskInvocationResult"/> — the error boundary decides. <c>Result.Fail</c> is
    /// reserved for pre-flight failures on the remote path.
    /// </summary>
    Task<Result<TaskInvocationResult>> DispatchAsync(
        WorkflowTask task,
        string wireTaskType,
        TaskEnvelope envelope,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default);
}
