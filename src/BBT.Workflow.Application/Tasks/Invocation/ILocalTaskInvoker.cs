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

/// <summary>Resolves the in-process invoker for a wire task type, if one is registered.</summary>
public interface ILocalTaskInvokerRegistry
{
    ILocalTaskInvoker? Get(string taskType);

    bool Has(string taskType);
}
