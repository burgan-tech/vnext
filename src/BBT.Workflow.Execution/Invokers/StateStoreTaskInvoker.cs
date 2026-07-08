using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr state store task invoker - stateless execution with strongly-typed binding.
/// Supports the get, set and delete commands against a Dapr state store.
/// When the binding does not specify a store name, the runtime's
/// <c>DAPR_STATE_STORE_NAME</c> configuration value is used so each runtime
/// targets its own state store component.
/// All task-supplied keys are stored under the fixed <c>custom:</c> prefix to
/// namespace task-written entries away from engine-owned cache keys sharing the
/// same store (query-matched keys come back from the store already prefixed and
/// are used as-is).
/// </summary>
public sealed class StateStoreTaskInvoker : ITaskInvoker<StateStoreBinding>
{
    // Command constants (case-insensitive match against binding.Command).
    private const string GetCommand = "get";
    private const string SetCommand = "set";
    private const string DeleteCommand = "delete";

    private const string StateStoreNameConfigKey = "DAPR_STATE_STORE_NAME";

    /// <summary>
    /// Fixed namespace prefix applied to every task-supplied key, preventing
    /// collisions with engine-owned cache keys in the shared state store.
    /// </summary>
    private const string KeyPrefix = "custom:";

    private readonly DaprClient _daprClient;
    private readonly string? _defaultStoreName;
    private readonly ITaskMetrics _metrics;
    private readonly ILogger<StateStoreTaskInvoker> _logger;

    public StateStoreTaskInvoker(
        DaprClient daprClient,
        IConfiguration configuration,
        ILogger<StateStoreTaskInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
        _defaultStoreName = configuration[StateStoreNameConfigKey];
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

        var storeName = !string.IsNullOrWhiteSpace(binding.StoreName)
            ? binding.StoreName
            : _defaultStoreName;

        if (string.IsNullOrWhiteSpace(storeName))
        {
            stopwatch.Stop();
            return TaskInvocationResult.Failure(
                error: "State store name is not configured: set 'storeName' in the task config " +
                       $"or the {StateStoreNameConfigKey} configuration value",
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

        var consistency = ParseConsistency(binding.Consistency);
        var metadata = BuildMetadata(binding, includeTtl: false);

        var (value, etag) = await _daprClient.GetStateAndETagAsync<JsonElement>(
            storeName,
            PrefixKey(binding.Key),
            consistency,
            metadata,
            cancellationToken);

        stopwatch.Stop();

        var found = value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

        return TaskInvocationResult.Success(
            data: found ? (object?)value : null,
            body: found ? value.GetRawText() : null,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, storeName, extra: new()
            {
                ["Found"] = found,
                ["ETag"] = etag ?? string.Empty
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
        var metadata = BuildMetadata(binding, includeTtl: true);
        var stateOptions = BuildStateOptions(binding);

        bool saved = true;
        if (!string.IsNullOrEmpty(binding.ETag))
        {
            saved = await _daprClient.TrySaveStateAsync(
                storeName,
                PrefixKey(binding.Key),
                value,
                binding.ETag,
                stateOptions,
                metadata,
                cancellationToken);
        }
        else
        {
            await _daprClient.SaveStateAsync(
                storeName,
                PrefixKey(binding.Key),
                value,
                stateOptions,
                metadata,
                cancellationToken);
        }

        stopwatch.Stop();

        return TaskInvocationResult.Success(
            data: new { Saved = saved, Key = PrefixKey(binding.Key) },
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
        var metadata = BuildMetadata(binding, includeTtl: false);

        // 1. Tag/pattern based deletion via the Dapr state Query API.
        if (!string.IsNullOrWhiteSpace(binding.Query))
        {
            List<string> matchedKeys;
            try
            {
                var queryResponse = await _daprClient.QueryStateAsync<JsonElement>(
                    storeName,
                    binding.Query,
                    metadata,
                    cancellationToken);

                matchedKeys = queryResponse?.Results?
                    .Select(r => r.Key)
                    .Where(k => k != null && k.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    .ToList() ?? new List<string>();
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

            var deletedByQuery = await DeleteKeysAsync(storeName, matchedKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, storeName, stopwatch, deletedByQuery);
        }

        // 2. Bulk key list deletion (task-supplied keys are namespaced).
        if (binding.Keys is { Count: > 0 })
        {
            var prefixedKeys = binding.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(PrefixKey)
                .ToList();
            var deleted = await DeleteKeysAsync(storeName, prefixedKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, storeName, stopwatch, deleted);
        }

        // 3. Single key deletion.
        if (!string.IsNullOrWhiteSpace(binding.Key))
        {
            var stateOptions = BuildStateOptions(binding);
            await _daprClient.DeleteStateAsync(
                storeName,
                PrefixKey(binding.Key),
                stateOptions,
                metadata,
                cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, storeName, stopwatch, 1);
        }

        return MissingFieldFailure(binding, storeName, stopwatch,
            "delete requires one of 'key', 'keys' or 'query'");
    }

    private async Task<int> DeleteKeysAsync(
        string storeName,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return 0;
        }

        var items = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => new BulkDeleteStateItem(k, etag: string.Empty))
            .ToList();

        if (items.Count == 0)
        {
            return 0;
        }

        await _daprClient.DeleteBulkStateAsync(storeName, items, cancellationToken);
        return items.Count;
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

    /// <summary>
    /// Builds the Dapr operation metadata, merging any caller-supplied metadata and,
    /// when requested, the <c>ttlInSeconds</c> entry.
    /// </summary>
    private static Dictionary<string, string>? BuildMetadata(StateStoreBinding binding, bool includeTtl)
    {
        Dictionary<string, string>? metadata = binding.Metadata is { Count: > 0 }
            ? new Dictionary<string, string>(binding.Metadata)
            : null;

        if (includeTtl && binding.TtlInSeconds is { } ttl)
        {
            metadata ??= new Dictionary<string, string>();
            metadata["ttlInSeconds"] = ttl.ToString();
        }

        return metadata;
    }

    private static StateOptions? BuildStateOptions(StateStoreBinding binding)
    {
        var concurrency = ParseConcurrency(binding.Concurrency);
        var consistency = ParseConsistency(binding.Consistency);

        if (concurrency is null && consistency is null)
        {
            return null;
        }

        return new StateOptions
        {
            Concurrency = concurrency,
            Consistency = consistency
        };
    }

    /// <summary>
    /// Applies the fixed <see cref="KeyPrefix"/> namespace to a task-supplied key.
    /// </summary>
    private static string PrefixKey(string key) => KeyPrefix + key;

    private static ConsistencyMode? ParseConsistency(string? consistency) =>
        consistency?.ToLowerInvariant() switch
        {
            "strong" => ConsistencyMode.Strong,
            "eventual" => ConsistencyMode.Eventual,
            _ => null
        };

    private static ConcurrencyMode? ParseConcurrency(string? concurrency) =>
        concurrency?.ToLowerInvariant() switch
        {
            "firstwrite" => ConcurrencyMode.FirstWrite,
            "lastwrite" => ConcurrencyMode.LastWrite,
            _ => null
        };

    private static Dictionary<string, object> BaseMetadata(
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
            metadata["Key"] = PrefixKey(binding.Key);
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
