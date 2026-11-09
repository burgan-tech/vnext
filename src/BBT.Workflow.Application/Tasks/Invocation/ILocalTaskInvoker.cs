using System.Text.Json;

namespace BBT.Workflow.Tasks.Invocation;

/// <summary>
/// Performs a task call in-process inside the Orchestration host, consuming the SAME prepared
/// binding the remote path would put on the wire. Mirrors the Execution service's
/// <c>ITaskInvoker</c> contract deliberately: binding in, result out, no workflow context and
/// no scripting, so the two paths cannot drift.
/// </summary>
public interface ILocalTaskInvoker
{
    /// <summary>Wire task type this invoker serves (<see cref="BBT.Workflow.Execution.TaskTypes"/>).</summary>
    string TaskType { get; }

    /// <summary>
    /// Executes the binding. Transport failures become failed results, never exceptions — the
    /// user-defined error boundary decides, exactly as on the remote path.
    /// </summary>
    Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A local invoker that also accepts its binding as the typed object the orchestrator mapped it to.
/// The <see cref="ILocalTaskInvoker"/> contract takes a <see cref="JsonElement"/> because that is the
/// wire shape; on the in-process path that meant serializing the mapped binding only to deserialize it
/// again one call later. The dispatcher uses this overload when the router picks Local and the invoker
/// implements it for the binding's type; the result must be identical to the <see cref="JsonElement"/>
/// overload's for the same binding.
/// </summary>
/// <typeparam name="TBinding">The binding type (e.g. <c>HttpTaskBinding</c>).</typeparam>
public interface ITypedLocalTaskInvoker<in TBinding> : ILocalTaskInvoker
    where TBinding : class
{
    /// <summary>Executes the typed binding. Same failure contract as <see cref="ILocalTaskInvoker.InvokeAsync"/>.</summary>
    Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        TBinding binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the in-process invoker for a wire task type, if one is registered.</summary>
public interface ILocalTaskInvokerRegistry
{
    ILocalTaskInvoker? Get(string taskType);

    bool Has(string taskType);
}
