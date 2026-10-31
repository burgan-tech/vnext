using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.Invocation;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Services;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr binding task invoker - stateless execution with strongly-typed binding.
/// Receives prepared binding name, operation, data and metadata.
/// </summary>
public sealed class DaprBindingTaskInvoker(
    DaprClient daprClient,
    ILogger<DaprBindingTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<DaprBindingTaskBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprBinding;

    /// <inheritdoc />
    public Type BindingType => typeof(DaprBindingTaskBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprBindingTaskBinding> descriptor,
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
        var typedBinding = binding.Deserialize<DaprBindingTaskBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprBindingTaskBinding");
        
        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprBindingTaskBinding binding,
        CancellationToken cancellationToken)
    {
        // The call, the response shaping and the failure reason are the shared core's (DaprBindingInvocation), the
        // same body the Orchestrator's in-process path runs; this host only meters and logs.
        Exception? failure = null;
        var result = await DaprBindingInvocation.ExecuteAsync(daprClient, binding, TaskType, cancellationToken, taskKey,
            onFailure: ex => failure = ex);
        var operation = result.Metadata?.TryGetValue("Operation", out var op) == true ? op?.ToString() ?? binding.Operation : binding.Operation;

        if (result.IsSuccess)
        {
            _metrics.RecordDaprBindingInvocation(binding.BindingName, operation, "success");
        }
        else if (result.Metadata?.ContainsKey("Cancelled") == true)
        {
            _metrics.RecordDaprBindingInvocation(binding.BindingName, operation, "cancelled");
            logger.LogWarning("Dapr binding invocation was cancelled: {BindingName}, Operation: {Operation}",
                binding.BindingName, operation);
        }
        else
        {
            _metrics.RecordDaprBindingInvocation(binding.BindingName, operation, "failure");
            logger.LogError(failure, "Dapr binding invocation failed: {BindingName}, Operation: {Operation}, Reason: {Reason}",
                binding.BindingName, operation, result.ErrorMessage);
        }

        return result;
    }
}
