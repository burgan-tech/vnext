using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Services;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure HTTP task invoker - stateless execution with strongly-typed binding.
/// Receives prepared URL, headers, body and delegates the call to
/// <see cref="HttpTaskInvocation"/> — the single HTTP-send implementation shared with the
/// Orchestrator's in-process external HTTP task (type 22), including the reserved
/// trace-header filtering and the trusted correlation/identity header enforcement — then adds
/// this host's logging and metrics on top of the returned result.
/// </summary>
public sealed class HttpTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<HttpTaskBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.Http;

    /// <inheritdoc />
    public Type BindingType => typeof(HttpTaskBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<HttpTaskBinding> descriptor,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(descriptor.TaskKey, descriptor.Binding, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<HttpTaskBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize HttpTaskBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        HttpTaskBinding binding,
        CancellationToken cancellationToken)
    {
        if (!binding.ValidateSSL)
        {
            logger.LogDebug("SSL certificate validation is disabled for HTTP task {TaskKey} - URL: {Url}",
                taskKey, binding.Url);
        }

        var result = await HttpTaskInvocation.SendAsync(
            httpClientFactory.CreateClient, binding, TaskType, cancellationToken, taskKey: taskKey);

        // The shared core never throws or logs; classify the failed results here so this host's
        // metrics and log lines stay exactly as they were before the core was extracted.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            logger.LogWarning("HTTP request was cancelled for task {TaskKey} - URL: {Url}", taskKey, binding.Url);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError("HTTP task invocation failed for {TaskKey} - URL: {Url}, Error: {Error}",
                taskKey, binding.Url, result.ErrorMessage);
        }

        return result;
    }
}
