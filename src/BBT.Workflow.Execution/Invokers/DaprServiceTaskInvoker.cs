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

        if (!result.IsSuccess && result.StatusCode is null)
            logger.LogError("Dapr service invocation failed for {TaskKey} - AppId: {AppId}, Error: {Error}",
                taskKey, binding.AppId, result.ErrorMessage);

        return result;
    }
}
