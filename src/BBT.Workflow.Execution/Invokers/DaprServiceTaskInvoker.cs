using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Metrics;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr service invocation task invoker - stateless execution with strongly-typed binding.
/// Receives prepared AppId, MethodName, HttpVerb, Headers and Body.
/// </summary>
public sealed class DaprServiceTaskInvoker(
    DaprServiceInvocationClient daprInvocation,
    ILogger<DaprServiceTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<DaprServiceBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprService;

    /// <inheritdoc />
    public Type BindingType => typeof(DaprServiceBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprServiceBinding> descriptor,
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
        var typedBinding = binding.Deserialize<DaprServiceBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprServiceBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprServiceBinding binding,
        CancellationToken cancellationToken)
    {
        var result = await DaprServiceInvocation.SendAsync(
            daprInvocation, binding, TaskType, cancellationToken, trusted: null, taskKey: taskKey);

        // The shared core never logs or records metrics; this host's classification stays here so
        // its dashboards are unchanged by the extraction. The core's SendAsync collapsed the three
        // original call sites (success/failure/cancelled) into one result, so the status string is
        // reconstructed the same way the original three branches produced it: success from
        // IsSuccess, cancelled from the "Cancelled" metadata flag the core stamps identically to
        // HttpTaskInvocation, failure otherwise.
        var status = result.IsSuccess
            ? "success"
            : HttpTaskInvocation.WasCancelled(result) ? "cancelled" : "failure";
        _metrics.RecordDaprServiceInvocation(binding.AppId, binding.MethodName, status);

        // The shared core never logs; restore the original two-branch split (Warning for a
        // cancellation, Error for everything else) instead of collapsing both into one Error
        // line — a cancellation is ordinary traffic (a caller timing out, an instance cancelled
        // mid-call) and must not trip anything alerting on this invoker's Error rate. Same shape
        // HttpTaskInvoker already uses for the HTTP path extracted the same way.
        if (!result.IsSuccess && HttpTaskInvocation.WasCancelled(result))
        {
            logger.LogWarning("Dapr service invocation was cancelled for task {TaskKey} - AppId: {AppId}",
                taskKey, binding.AppId);
        }
        else if (!result.IsSuccess && result.StatusCode is null)
        {
            logger.LogError("Dapr service invocation failed for {TaskKey} - AppId: {AppId}, Error: {Error}, ExceptionType: {ExceptionType}",
                taskKey, binding.AppId, result.ErrorMessage, ExceptionTypeOf(result));
        }

        return result;
    }

    /// <summary>
    /// Reads the transport-failure exception type name the shared core stamps into
    /// <c>Metadata["ExceptionType"]</c> on the unhandled-exception path. The core swallows the
    /// exception itself by contract (it returns a <see cref="TaskInvocationResult"/>, never
    /// throws), so this is the only way the wrapper's error log can still name the failure type —
    /// the same trade already made for <c>HttpTaskInvoker</c> when its core was extracted.
    /// </summary>
    private static string ExceptionTypeOf(TaskInvocationResult result) =>
        result.Metadata?.TryGetValue("ExceptionType", out var value) == true && value is string type
            ? type
            : string.Empty;
}
