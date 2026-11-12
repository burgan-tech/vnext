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

    /// <summary>
    /// Same decision and result contract as the envelope overload, for a binding the caller holds as
    /// its mapped object. Local, with an <see cref="ITypedLocalTaskInvoker{TBinding}"/> registered for
    /// the wire type: the object is handed over as-is (no JSON round trip). Every other route —
    /// Remote above all — serializes it into the exact envelope the envelope overload would have
    /// received (<c>TaskType = wireTaskType</c>, <c>TaskKey = task.Key</c>, binding serialized with
    /// its runtime type).
    /// </summary>
    Task<Result<TaskInvocationResult>> DispatchAsync<TBinding>(
        WorkflowTask task,
        string wireTaskType,
        TBinding binding,
        TaskTraceContext traceContext,
        CancellationToken cancellationToken = default)
        where TBinding : class;
}
