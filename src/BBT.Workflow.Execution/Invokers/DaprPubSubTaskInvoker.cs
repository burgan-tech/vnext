using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr PubSub task invoker - stateless execution with strongly-typed binding.
/// Receives prepared PubSubName, Topic, Data and Metadata.
/// </summary>
public sealed class DaprPubSubTaskInvoker : ITaskInvoker<DaprPubSubBinding>
{
    private readonly DaprClient _daprClient;
    private readonly ITaskMetrics _metrics;
    private readonly ILogger<DaprPubSubTaskInvoker> _logger;

    public DaprPubSubTaskInvoker(
        DaprClient daprClient,
        ILogger<DaprPubSubTaskInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprPubSub;

    /// <inheritdoc />
    public Type BindingType => typeof(DaprPubSubBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprPubSubBinding> descriptor,
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
        var typedBinding = binding.Deserialize<DaprPubSubBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprPubSubBinding");
        
        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprPubSubBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Parse body as event data
            object? data = null;
            if (!string.IsNullOrEmpty(binding.Body))
            {
                data = JsonSerializer.Deserialize<object>(binding.Body);
            }

            var metadata = binding.Metadata ?? new Dictionary<string, string>();

            await _daprClient.PublishEventAsync(
                binding.PubSubName,
                binding.TopicName,
                data,
                metadata,
                cancellationToken);

            stopwatch.Stop();
            _metrics.RecordDaprPubSubPublish(binding.PubSubName, binding.TopicName, "success");

            return TaskInvocationResult.Success(
                data: new { Published = true, Message = "Event published successfully" },
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["PubSubName"] = binding.PubSubName,
                    ["Topic"] = binding.TopicName
                });
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordDaprPubSubPublish(binding.PubSubName, binding.TopicName, "cancelled");
            _logger.LogWarning("Dapr PubSub operation was cancelled: {PubSubName}/{Topic}",
                binding.PubSubName, binding.TopicName);

            return TaskInvocationResult.Failure(
                error: "Dapr PubSub operation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["PubSubName"] = binding.PubSubName,
                    ["Topic"] = binding.TopicName,
                    ["Cancelled"] = true,
                    ["Published"] = false,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordDaprPubSubPublish(binding.PubSubName, binding.TopicName, "failure");
            _logger.LogError(ex, "Dapr PubSub publish failed: {PubSubName}/{Topic}",
                binding.PubSubName, binding.TopicName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["PubSubName"] = binding.PubSubName,
                    ["Topic"] = binding.TopicName,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty,
                    ["Published"] = false
                });
        }
    }
}
