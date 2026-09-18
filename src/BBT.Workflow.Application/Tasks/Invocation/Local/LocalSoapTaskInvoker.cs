using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Runs a SOAP task in-process through <see cref="SoapInvocation"/>, the body the Execution
/// service's <c>SoapTaskInvoker</c> also uses — SOAP 1.1/1.2 Content-Type and SOAPAction handling,
/// XML parsing and Fault detection are defined once. A SOAP Fault is a failed result, not an
/// exception, exactly as on the remote path.
/// </summary>
public sealed class LocalSoapTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<LocalSoapTaskInvoker> logger) : ILocalTaskInvoker
{
    /// <inheritdoc />
    public string TaskType => TaskTypes.Soap;

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<SoapTaskBinding>();
        if (typedBinding is null)
        {
            return TaskInvocationResult.Failure(
                error: $"SOAP task {taskKey} produced an empty binding.",
                taskType: TaskTypes.Soap);
        }

        var result = await SoapInvocation.SendAsync(
            httpClientFactory.CreateClient,
            typedBinding,
            TaskTypes.Soap,
            cancellationToken,
            LocalInvocationResultMapper.ToWireTraceContext(traceContext),
            taskKey);

        // Cancellation is ordinary traffic (a caller timing out, an instance cancelled mid-call) and
        // must not trip anything alerting on this invoker's Error rate — the same split the Execution
        // host's invokers and LocalHttpTaskInvoker make. WasCancelled reads the Metadata["Cancelled"]
        // flag the shared core stamps; do not invent a different detection.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.LocalTaskInvocationCancelled(taskKey, TaskTypes.Soap);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            logger.LocalTaskInvocationFailed(
                taskKey, TaskTypes.Soap, result.ErrorMessage ?? "Unknown error");
        }

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }
}
