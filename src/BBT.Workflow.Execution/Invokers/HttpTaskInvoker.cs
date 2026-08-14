using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure HTTP task invoker - stateless execution with strongly-typed binding.
/// Receives prepared URL, headers, body and delegates the call to
/// <see cref="HttpTaskInvocation"/> — the single HTTP-send implementation shared with the
/// Orchestrator's in-process external HTTP task (type 21) — then adds this host's logging and
/// metrics on top of the returned result.
/// </summary>
public sealed class HttpTaskInvoker(
    IHttpClientFactory httpClientFactory,
    ILogger<HttpTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<HttpTaskBinding>
{
    // Kept local so the stateless Execution package does not acquire a Domain dependency.
    // These names are the public HTTP contract defined by TelemetryConstants in vNext Domain.
    private const string WorkflowInstanceHeader = "X-Workflow-Instance-Id";
    private const string CorrelationHeader = "X-Correlation-Id";
    private const string SubHeader = "sub";
    private const string ActSubHeader = "act_sub";
    private const string WorkflowInstanceBaggage = "workflow.instance.id";
    private const string CorrelationBaggage = "correlation.id";
    private const string SubBaggage = "sub";
    private const string ActSubBaggage = "act.sub";

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
            httpClientFactory.CreateClient, binding, TaskType, cancellationToken);

        ApplyTrustedCorrelationHeaders(request);

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

    private static void ApplyTrustedCorrelationHeaders(HttpRequestMessage request)
    {
        // Mapping-provided values are untrusted and must never be allowed to spoof
        // the workflow context established by vNext.
        request.Headers.Remove(WorkflowInstanceHeader);
        request.Headers.Remove(CorrelationHeader);
        request.Headers.Remove(SubHeader);
        request.Headers.Remove(ActSubHeader);

        var workflowInstance = Activity.Current?.GetBaggageItem(WorkflowInstanceBaggage);
        if (Guid.TryParse(workflowInstance, out var workflowInstanceId)
            && workflowInstanceId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(
                WorkflowInstanceHeader,
                workflowInstanceId.ToString("D").ToLowerInvariant());
        }

        var correlation = Activity.Current?.GetBaggageItem(CorrelationBaggage);
        if (Guid.TryParseExact(correlation, "N", out var correlationId)
            && correlationId != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId.ToString("N"));
        }

        var subject = Activity.Current?.GetBaggageItem(SubBaggage);
        if (IsSafeIdentityClaim(subject))
        {
            request.Headers.TryAddWithoutValidation(SubHeader, subject);
        }

        var actSub = Activity.Current?.GetBaggageItem(ActSubBaggage);
        if (IsSafeIdentityClaim(actSub))
        {
            request.Headers.TryAddWithoutValidation(ActSubHeader, actSub);
        }
    }

    private static bool IsSafeIdentityClaim(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            {
                return false;
            }
        }

        return true;
    }

}
