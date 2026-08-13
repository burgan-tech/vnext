using BBT.Workflow.Execution.Bindings;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Performs an HTTP task call in-process, inside the Orchestration host, instead of routing it
/// through the Execution service's <c>/execution/invoke</c> hop. Consumes the same
/// <see cref="HttpTaskBinding"/> the remote path would put on the wire and mirrors the Execution
/// service's <c>HttpTaskInvoker</c> semantics (header/Content-Type splitting, body resolution,
/// per-request timeout, SSL-validation client selection, accepted-status-code matching, response
/// parsing), so output mapping scripts observe identical shapes for both task types.
/// </summary>
public interface ILocalHttpTaskInvoker
{
    /// <summary>
    /// Executes the HTTP request described by the binding.
    /// </summary>
    /// <param name="taskKey">Task key, for logging only.</param>
    /// <param name="binding">The prepared HTTP binding (URL, method, headers, body, options).</param>
    /// <param name="cancellationToken">Pipeline cancellation token.</param>
    /// <returns>The invocation result; transport failures become failed results, never exceptions.</returns>
    Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        HttpTaskBinding binding,
        CancellationToken cancellationToken = default);
}
