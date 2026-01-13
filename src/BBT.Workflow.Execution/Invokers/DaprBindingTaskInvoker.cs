using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
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
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var metadata = binding.Metadata ?? new Dictionary<string, string>();
            
            // Get operation from metadata (method), override task operation if present
            var operation = metadata.TryGetValue("method", out var method) && !string.IsNullOrEmpty(method)
                ? method
                : binding.Operation;

            // Remove internal metadata keys before sending
            var cleanMetadata = metadata
                .Where(kvp => kvp.Key != "method" && kvp.Key != "ForwardingHeaders")
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Parse body data
            object? data = string.IsNullOrEmpty(binding.Body)
                ? null
                : JsonSerializer.Deserialize<object>(binding.Body);

            await daprClient.InvokeBindingAsync(
                binding.BindingName,
                operation,
                data,
                cleanMetadata,
                cancellationToken);

            stopwatch.Stop();
            _metrics.RecordDaprBindingInvocation(binding.BindingName, operation, "success");

            return TaskInvocationResult.Success(
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = binding.BindingName,
                    ["Operation"] = operation
                });
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordDaprBindingInvocation(binding.BindingName, binding.Operation, "cancelled");
            logger.LogWarning("Dapr binding invocation was cancelled: {BindingName}, Operation: {Operation}",
                binding.BindingName, binding.Operation);

            return TaskInvocationResult.Failure(
                error: "Dapr binding invocation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = binding.BindingName,
                    ["Operation"] = binding.Operation,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordDaprBindingInvocation(binding.BindingName, binding.Operation, "failure");
            logger.LogError(ex, "Dapr binding invocation failed: {BindingName}, Operation: {Operation}",
                binding.BindingName, binding.Operation);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = binding.BindingName,
                    ["Operation"] = binding.Operation,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }
}
