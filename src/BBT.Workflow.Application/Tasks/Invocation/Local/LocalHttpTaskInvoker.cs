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
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        HttpTaskBinding binding,
        TaskTraceContext? traceContext,
        string taskTypeLabel,
        CancellationToken cancellationToken = default)
    {
        if (!binding.ValidateSSL)
            logger.ExternalHttpTaskSslValidationDisabled(taskKey, binding.Url);

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
            logger.ExternalHttpTaskRequestCancelled(taskKey, binding.Url);
        else if (!result.IsSuccess && result.StatusCode is null)
            logger.ExternalHttpTaskRequestFailed(taskKey, binding.Url, result.ErrorMessage ?? "Unknown error");

        return LocalInvocationResultMapper.ToOrchestratorResult(result);
    }
}
