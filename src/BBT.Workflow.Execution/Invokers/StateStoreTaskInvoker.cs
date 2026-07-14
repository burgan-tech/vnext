using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.StateStores;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Dapr state store task invoker. Supports the get, set and delete commands. State-store access is
/// delegated to the shared <see cref="IStateStoreClient"/> (store-name resolution, the <c>custom:</c>
/// key prefix, TTL, consistency/concurrency), so this invoker only routes commands and shapes results.
/// </summary>
public sealed class StateStoreTaskInvoker : ITaskInvoker<StateStoreBinding>
{
    // Command constants (case-insensitive match against binding.Command).
    private const string GetCommand = "get";
    private const string SetCommand = "set";
    private const string DeleteCommand = "delete";

    private readonly IStateStoreClient _stateStore;
    private readonly ITaskMetrics _metrics;
    private readonly ILogger<StateStoreTaskInvoker> _logger;

    public StateStoreTaskInvoker(
        IStateStoreClient stateStore,
        ILogger<StateStoreTaskInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _stateStore = stateStore;
        _logger = logger;
        _metrics = metrics ?? NullTaskMetrics.Instance;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.StateStore;

    /// <inheritdoc />
    public Type BindingType => typeof(StateStoreBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<StateStoreBinding> descriptor,
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
        var typedBinding = binding.Deserialize<StateStoreBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize StateStoreBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        StateStoreBinding binding,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var command = binding.Command ?? string.Empty;

        var storeName = _stateStore.ResolveStoreName(binding.StoreName);

        if (string.IsNullOrWhiteSpace(storeName))
        {
            stopwatch.Stop();
            return TaskInvocationResult.Failure(
                error: "State store name is not configured: set 'storeName' in the task config " +
                       "or the DAPR_STATE_STORE_NAME configuration value",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: BaseMetadata(binding, storeName: string.Empty));
        }

        try
        {
            TaskInvocationResult result = command.ToLowerInvariant() switch
            {
                GetCommand => await GetAsync(binding, storeName, stopwatch, cancellationToken),
                SetCommand => await SetAsync(binding, storeName, stopwatch, cancellationToken),
                DeleteCommand => await DeleteAsync(binding, storeName, stopwatch, cancellationToken),
                _ => UnsupportedCommand(binding, storeName, stopwatch)
            };

            _metrics.RecordStateStoreOperation(
                storeName,
                command,
                result.IsSuccess ? "success" : "failure");

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordStateStoreOperation(storeName, command, "cancelled");
            _logger.LogWarning("State store operation was cancelled: {StoreName}/{Command}",
                storeName, command);

            return TaskInvocationResult.Failure(
                error: "State store operation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: BaseMetadata(binding, storeName, extra: new() { ["Cancelled"] = true }));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordStateStoreOperation(storeName, command, "failure");
            _logger.LogError(ex, "State store operation failed: {StoreName}/{Command}",
                storeName, command);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: BaseMetadata(binding, storeName, extra: new()
                {
                    ["ExceptionType"] = ex.GetType().Name
                }));
        }
    }

    private async Task<TaskInvocationResult> GetAsync(
        StateStoreBinding binding,
        string storeName,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return MissingFieldFailure(binding, storeName, stopwatch, "get requires 'key'");
        }

        var entry = await _stateStore.GetAsync(
            storeName, binding.Key, binding.Consistency, binding.Metadata, cancellationToken);

        stopwatch.Stop();

        return TaskInvocationResult.Success(
            data: entry.Found ? (object?)entry.Value : null,
            body: entry.Found ? entry.Value.GetRawText() : null,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, storeName, extra: new()
            {
                ["Found"] = entry.Found,
                ["ETag"] = entry.ETag ?? string.Empty
            }));
    }

    private async Task<TaskInvocationResult> SetAsync(
        StateStoreBinding binding,
        string storeName,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return MissingFieldFailure(binding, storeName, stopwatch, "set requires 'key'");
        }

        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            return MissingFieldFailure(binding, storeName, stopwatch, "set requires 'value'");
        }

        var value = JsonSerializer.Deserialize<JsonElement>(binding.Value);
        var saved = await _stateStore.SetAsync(
            storeName, binding.Key, value, binding.TtlInSeconds, binding.Consistency,
            binding.Concurrency, binding.ETag, binding.Metadata, cancellationToken);

        stopwatch.Stop();

        return TaskInvocationResult.Success(
            data: new { Saved = saved, Key = _stateStore.PrefixKey(binding.Key) },
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, storeName, extra: new() { ["Saved"] = saved }));
    }

    private async Task<TaskInvocationResult> DeleteAsync(
        StateStoreBinding binding,
        string storeName,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        // 1. Tag/pattern based deletion via the Dapr state Query API.
        if (!string.IsNullOrWhiteSpace(binding.Query))
        {
            List<string> matchedKeys;
            try
            {
                matchedKeys = (await _stateStore.QueryPrefixedKeysAsync(
                    storeName, binding.Query, binding.Metadata, cancellationToken)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                return TaskInvocationResult.Failure(
                    error: $"Query-based deletion is not supported by state store '{storeName}': {ex.Message}",
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    metadata: BaseMetadata(binding, storeName, extra: new()
                    {
                        ["ExceptionType"] = ex.GetType().Name
                    }));
            }

            var deletedByQuery = await _stateStore.DeleteBulkAsync(storeName, matchedKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, storeName, stopwatch, deletedByQuery);
        }

        // 2. Bulk key list deletion (task-supplied keys are namespaced).
        if (binding.Keys is { Count: > 0 })
        {
            var prefixedKeys = binding.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(_stateStore.PrefixKey)
                .ToList();
            var deleted = await _stateStore.DeleteBulkAsync(storeName, prefixedKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, storeName, stopwatch, deleted);
        }

        // 3. Single key deletion.
        if (!string.IsNullOrWhiteSpace(binding.Key))
        {
            await _stateStore.DeleteAsync(
                storeName, binding.Key, binding.Consistency, binding.Concurrency, binding.Metadata, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, storeName, stopwatch, 1);
        }

        return MissingFieldFailure(binding, storeName, stopwatch,
            "delete requires one of 'key', 'keys' or 'query'");
    }

    private TaskInvocationResult DeleteSuccess(
        StateStoreBinding binding,
        string storeName,
        Stopwatch stopwatch,
        int deletedCount) =>
        TaskInvocationResult.Success(
            data: new { DeletedCount = deletedCount },
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, storeName, extra: new() { ["DeletedCount"] = deletedCount }));

    private TaskInvocationResult UnsupportedCommand(
        StateStoreBinding binding,
        string storeName,
        Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return TaskInvocationResult.Failure(
            error: $"Unsupported state store command: '{binding.Command}'. " +
                   $"Expected one of: {GetCommand}, {SetCommand}, {DeleteCommand}.",
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, storeName));
    }

    private TaskInvocationResult MissingFieldFailure(
        StateStoreBinding binding,
        string storeName,
        Stopwatch stopwatch,
        string message)
    {
        stopwatch.Stop();
        return TaskInvocationResult.Failure(
            error: message,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, storeName));
    }

    private Dictionary<string, object> BaseMetadata(
        StateStoreBinding binding,
        string storeName,
        Dictionary<string, object>? extra = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["StoreName"] = storeName,
            ["Command"] = binding.Command ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(binding.Key))
        {
            metadata["Key"] = _stateStore.PrefixKey(binding.Key);
        }

        if (extra is not null)
        {
            foreach (var kvp in extra)
            {
                metadata[kvp.Key] = kvp.Value;
            }
        }

        return metadata;
    }
}
