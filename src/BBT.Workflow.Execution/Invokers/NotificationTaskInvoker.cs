using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Notification;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Task invoker for notification tasks using Dapr bindings.
/// Resolves the binding component at runtime via INotificationBindingResolver
/// and supports multiple binding types (HTTP, MQTT, SignalR, Kafka, etc.).
/// </summary>
public sealed class NotificationTaskInvoker : ITaskInvoker<NotificationBinding>
{
    private readonly DaprClient _daprClient;
    private readonly INotificationBindingResolver _bindingResolver;
    private readonly ITaskMetrics _metrics;
    private readonly ILogger<NotificationTaskInvoker> _logger;

    /// <summary>
    /// Default operation for binding invocation.
    /// </summary>
    private const string DefaultOperation = "create";

    /// <summary>
    /// Initializes a new instance of the <see cref="NotificationTaskInvoker"/> class.
    /// </summary>
    /// <param name="daprClient">The Dapr client.</param>
    /// <param name="bindingResolver">The notification binding resolver.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="metrics">Optional task metrics.</param>
    public NotificationTaskInvoker(
        DaprClient daprClient,
        INotificationBindingResolver bindingResolver,
        ILogger<NotificationTaskInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _bindingResolver = bindingResolver;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.Notification;

    /// <inheritdoc />
    public Type BindingType => typeof(NotificationBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<NotificationBinding> descriptor,
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
        var typedBinding = binding.Deserialize<NotificationBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize NotificationBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        NotificationBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Resolve the binding component at runtime
        NotificationBindingInfo bindingInfo;
        try
        {
            bindingInfo = await _bindingResolver.ResolveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to resolve notification binding for task {TaskKey}", taskKey);
            return TaskInvocationResult.Failure(
                error: $"Failed to resolve notification binding: {ex.Message}",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["ExceptionType"] = ex.GetType().Name
                });
        }

        var bindingName = bindingInfo.Name;
        var bindingKind = bindingInfo.Kind.ToString();

        try
        {
            // Build the notification payload from binding data
            var payload = BuildPayload(binding);
            var data = JsonSerializer.SerializeToUtf8Bytes(payload);

            // Build binding metadata combining task metadata and binding-kind-specific defaults
            var metadata = BuildMetadata(binding, bindingInfo);

            // Determine operation
            var operation = !string.IsNullOrEmpty(binding.Operation) 
                ? binding.Operation 
                : DefaultOperation;

            _logger.LogInformation(
                "Executing notification task via Dapr binding. " +
                "Binding: {BindingName}, Kind: {BindingKind}, Operation: {Operation}, TaskKey: {TaskKey}",
                bindingName, bindingKind, operation, taskKey);

            // Invoke the Dapr binding
            await _daprClient.InvokeBindingAsync(
                bindingName,
                operation,
                data,
                metadata,
                cancellationToken);

            stopwatch.Stop();
            _metrics.RecordNotificationInvocation(bindingName, bindingKind, "success");
            
            return TaskInvocationResult.Success(
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = bindingName,
                    ["BindingKind"] = bindingKind,
                    ["Operation"] = operation,
                    ["Recipients"] = binding.To ?? Array.Empty<string>()
                });
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordNotificationInvocation(bindingName, bindingKind, "cancelled");

            _logger.LogWarning(
                "Notification task cancelled. Binding: {BindingName}, TaskKey: {TaskKey}",
                bindingName, taskKey);

            return TaskInvocationResult.Failure(
                error: "Notification task was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = bindingName,
                    ["BindingKind"] = bindingKind,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordNotificationInvocation(bindingName, bindingKind, "failure");

            _logger.LogError(ex,
                "Notification task failed. Binding: {BindingName}, TaskKey: {TaskKey}, Error: {Error}",
                bindingName, taskKey, ex.Message);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["BindingName"] = bindingName,
                    ["BindingKind"] = bindingKind,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }

    /// <summary>
    /// Builds the notification payload from the binding data.
    /// </summary>
    private static object BuildPayload(NotificationBinding binding)
    {
        // If body is provided (from mapping), parse it as the payload
        if (!string.IsNullOrEmpty(binding.Body))
        {
            try
            {
                return JsonSerializer.Deserialize<object>(binding.Body) ?? new { };
            }
            catch
            {
                // If parsing fails, use body as-is
                return new { message = binding.Body };
            }
        }

        // Build a default payload structure from individual fields
        return new
        {
            to = binding.To,
            subject = binding.Subject
        };
    }

    /// <summary>
    /// Builds the binding metadata combining task metadata and binding-kind-specific defaults.
    /// </summary>
    private static Dictionary<string, string> BuildMetadata(
        NotificationBinding binding,
        NotificationBindingInfo bindingInfo)
    {
        // Start with task-defined metadata
        var metadata = binding.Metadata != null
            ? new Dictionary<string, string>(binding.Metadata)
            : new Dictionary<string, string>();

        // Add binding-kind-specific defaults if not already present
        switch (bindingInfo.Kind)
        {
            case NotificationBindingKind.Http:
                // HTTP method for the binding request
                // Note: URL is automatically resolved from the Dapr component YAML configuration
                // Dapr sidecar reads the URL from spec.metadata and uses it for the request
                metadata.TryAdd("method", "POST");
                break;

            case NotificationBindingKind.Mqtt:
                if (!metadata.ContainsKey("topic"))
                {
                    var mqttTopic = bindingInfo.GetMetadata("topic") ?? "notifications";
                    metadata["topic"] = mqttTopic;
                }
                break;

            case NotificationBindingKind.SignalR:
                if (!metadata.ContainsKey("hub"))
                {
                    var signalRHub = bindingInfo.GetMetadata("hub") ?? "notifications";
                    metadata["hub"] = signalRHub;
                }
                break;

            case NotificationBindingKind.Kafka:
                if (!metadata.ContainsKey("topic"))
                {
                    var kafkaTopic = bindingInfo.GetMetadata("topic") ?? "notifications";
                    metadata["topic"] = kafkaTopic;
                }
                break;

            case NotificationBindingKind.RabbitMq:
                if (!metadata.ContainsKey("routingKey"))
                {
                    var routingKey = bindingInfo.GetMetadata("routingKey") ?? "notifications";
                    metadata["routingKey"] = routingKey;
                }
                break;

            case NotificationBindingKind.Redis:
                if (!metadata.ContainsKey("channel"))
                {
                    var channel = bindingInfo.GetMetadata("channel") ?? "notifications";
                    metadata["channel"] = channel;
                }
                break;
        }

        return metadata;
    }
}
