using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// SOAP task invoker — stateless execution with strongly-typed binding.
/// Handles SOAP 1.1 and 1.2 protocol differences for Content-Type and SOAPAction headers,
/// parses XML responses, and detects SOAP Fault elements.
/// </summary>
public sealed class SoapTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<SoapTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<SoapTaskBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.Soap;

    /// <inheritdoc />
    public Type BindingType => typeof(SoapTaskBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<SoapTaskBinding> descriptor,
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
        var typedBinding = binding.Deserialize<SoapTaskBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize SoapTaskBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        SoapTaskBinding binding,
        CancellationToken cancellationToken)
    {
        // The shared core doesn't log; this line was on the pre-extraction CreateHttpClient and
        // must fire on the same condition (ValidateSSL false) before the call goes out, not after —
        // same placement HttpTaskInvoker uses for its own copy of this guard.
        if (!binding.ValidateSSL)
        {
            logger.LogDebug("SSL certificate validation is disabled for SOAP task {TaskKey} - URL: {Url}",
                taskKey, binding.Url);
        }

        var result = await SoapInvocation.SendAsync(
            httpClientFactory.CreateClient, binding, TaskType, cancellationToken, trusted: null, taskKey: taskKey);

        // The shared core never logs or records metrics; this host's classification stays here so
        // its dashboards and log lines are unchanged by the extraction. Cancellation is carved out
        // of the failure branch (Warning, not Error) — it is ordinary traffic (a caller timing out,
        // an instance cancelled mid-call) and must not trip anything alerting on this invoker's
        // Error rate. Same shape DaprServiceTaskInvoker already uses for the Dapr path extracted
        // the same way.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            _metrics.RecordTaskExecution(TaskType, "cancelled");
            logger.LogWarning("SOAP request was cancelled for task {TaskKey} - URL: {Url}", taskKey, binding.Url);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            _metrics.RecordTaskExecution(TaskType, "failure");
            logger.LogError("SOAP task invocation failed for {TaskKey} - URL: {Url}, Error: {Error}, ExceptionType: {ExceptionType}",
                taskKey, binding.Url, result.ErrorMessage, ExceptionTypeOf(result));
        }

        return result;
    }

    /// <summary>
    /// Reads the transport-failure exception type name the shared core stamps into
    /// <c>Metadata["ExceptionType"]</c> on the unhandled-exception path. The core swallows the
    /// exception itself by contract (it returns a <see cref="TaskInvocationResult"/>, never
    /// throws), so this is the only way the wrapper's error log can still name the failure type —
    /// the same trade already made for <c>DaprServiceTaskInvoker</c> when its core was extracted.
    /// </summary>
    private static string ExceptionTypeOf(TaskInvocationResult result) =>
        result.Metadata?.TryGetValue("ExceptionType", out var value) == true && value is string type
            ? type
            : string.Empty;
}
