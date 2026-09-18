using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Invocation.Local;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Type-22 (<see cref="TaskType.ExternalHttp"/>) in-process HTTP invocation. Since issue #1007 the
/// body lives in <see cref="LocalHttpTaskInvoker"/>, which the type-6 local path also uses; this
/// wrapper only keeps the type-22 task-type label on the result so existing output mappings and
/// journal rows read exactly as before.
/// </summary>
public sealed class ExternalHttpTaskInvoker(LocalHttpTaskInvoker localHttp) : IExternalHttpTaskInvoker
{
    /// <inheritdoc />
    public Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        HttpTaskBinding binding,
        CancellationToken cancellationToken = default,
        TaskTraceContext? traceContext = null) =>
        localHttp.InvokeAsync(
            taskKey, binding, traceContext, TaskType.ExternalHttp.ToString(), cancellationToken);
}
