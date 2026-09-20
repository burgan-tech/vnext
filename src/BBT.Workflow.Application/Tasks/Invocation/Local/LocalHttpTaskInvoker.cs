using System.Text.Json;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Invocation.Local;

/// <summary>
/// Runs a type-6 HTTP task in-process, through <see cref="HttpTaskInvocation"/> — the single send
/// implementation the Execution service's <c>HttpTaskInvoker</c> also uses. Only the host differs:
/// no Dapr sidecar, no circuit breaker and no <c>ExecutionApi:InvocationTimeoutSeconds</c> layer,
/// so the task's own <c>timeoutSeconds</c> is the only bound below the job budget.
/// </summary>
public sealed class LocalHttpTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<LocalHttpTaskInvoker> logger) : ILocalTaskInvoker
{
    /// <summary>
    /// The type-22 wrapper's task-type label. Kept as the ONE condition that routes logging to the
    /// shipped 10108/10099/10098 events instead of the generic 1016x ones — see the typed
    /// <see cref="InvokeAsync(string?,HttpTaskBinding,TaskTraceContext?,string,CancellationToken)"/>
    /// overload's remarks.
    /// </summary>
    private static readonly string ExternalHttpTaskTypeLabel =
        BBT.Workflow.Definitions.TaskType.ExternalHttp.ToString();

    /// <inheritdoc />
    public string TaskType => TaskTypes.Http;

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        TaskTraceContext? traceContext,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<HttpTaskBinding>();
        if (typedBinding is null)
        {
            return TaskInvocationResult.Failure(
                error: $"HTTP task {taskKey} produced an empty HTTP binding.",
                taskType: TaskTypes.Http);
        }

        return await InvokeAsync(
            taskKey, typedBinding, traceContext, TaskTypes.Http, cancellationToken);
    }

    /// <summary>
    /// The typed entry point. Public so the type-22 wrapper (<c>ExternalHttpTaskInvoker</c>) can
    /// reuse this exact body with its own task-type label instead of keeping a second copy.
    /// </summary>
    /// <remarks>
    /// Logging branches on <paramref name="taskTypeLabel"/>: the type-22 (<c>ExternalHttp</c>)
    /// label keeps the shipped, already-alerted-on 10108/10099/10098 events completely unchanged —
    /// that path is live and must not move. Every other label — starting with type-6's plain
    /// <c>"http"</c> — uses the generic 1016x events every other local invoker (SOAP, Dapr service
    /// invocation, state store) already uses, so a type-6 HTTP task's SSL/cancellation/failure
    /// signal is not silently missing from an operator's 10161/10162/10163 alerts, and the line it
    /// does get does not falsely name it "external HTTP" (the type-22 task type).
    /// </remarks>
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        HttpTaskBinding binding,
        TaskTraceContext? traceContext,
        string taskTypeLabel,
        CancellationToken cancellationToken = default)
    {
        var isExternalHttpLabel = string.Equals(
            taskTypeLabel, ExternalHttpTaskTypeLabel, StringComparison.Ordinal);

        if (!binding.ValidateSSL)
        {
            if (isExternalHttpLabel)
                logger.ExternalHttpTaskSslValidationDisabled(taskKey, binding.Url);
            else
                logger.LocalTaskInvocationSslValidationDisabled(taskKey, taskTypeLabel, binding.Url);
        }

        var result = await HttpTaskInvocation.SendAsync(
            httpClientFactory.CreateClient,
            binding,
            taskTypeLabel,
            cancellationToken,
            LocalInvocationResultMapper.ToWireTraceContext(traceContext),
            taskKey);

        // The shared core never throws or logs; classify the failed results here so this host's
        // log lines carry the workflow-structured events.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            if (isExternalHttpLabel)
                logger.ExternalHttpTaskRequestCancelled(taskKey, binding.Url);
            else
                logger.LocalTaskInvocationCancelled(taskKey, taskTypeLabel);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            if (isExternalHttpLabel)
                logger.ExternalHttpTaskRequestFailed(taskKey, binding.Url, result.ErrorMessage ?? "Unknown error");
            else
                logger.LocalTaskInvocationFailed(
                    taskKey, taskTypeLabel, result.ErrorMessage ?? "Unknown error",
                    LocalInvocationResultMapper.HasExceptionType(result, out var exceptionType)
                        ? exceptionType
                        : null);
        }

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }
}
