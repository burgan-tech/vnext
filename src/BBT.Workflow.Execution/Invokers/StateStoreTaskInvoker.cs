using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Pure Dapr state store task invoker - stateless execution with strongly-typed binding.
/// Supports the get, set and delete commands against a Dapr state store.
/// </summary>
public sealed class StateStoreTaskInvoker : ITaskInvoker<StateStoreBinding>
{
    // Command constants (case-insensitive match against binding.Command).
    private const string GetCommand = "get";
    private const string SetCommand = "set";
    private const string DeleteCommand = "delete";

    private readonly DaprClient _daprClient;
    private readonly ITaskMetrics _metrics;
    private readonly ILogger<StateStoreTaskInvoker> _logger;

    public StateStoreTaskInvoker(
        DaprClient daprClient,
        ILogger<StateStoreTaskInvoker> logger,
        ITaskMetrics? metrics = null)
    {
        _daprClient = daprClient;
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

        try
        {
            TaskInvocationResult result = command.ToLowerInvariant() switch
            {
                GetCommand => await GetAsync(binding, stopwatch, cancellationToken),
                SetCommand => await SetAsync(binding, stopwatch, cancellationToken),
                DeleteCommand => await DeleteAsync(binding, stopwatch, cancellationToken),
                _ => UnsupportedCommand(binding, stopwatch)
            };

            _metrics.RecordStateStoreOperation(
                binding.StoreName,
                command,
                result.IsSuccess ? "success" : "failure");

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _metrics.RecordStateStoreOperation(binding.StoreName, command, "cancelled");
            _logger.LogWarning("State store operation was cancelled: {StoreName}/{Command}",
                binding.StoreName, command);

            return TaskInvocationResult.Failure(
                error: "State store operation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: BaseMetadata(binding, extra: new() { ["Cancelled"] = true }));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.RecordStateStoreOperation(binding.StoreName, command, "failure");
            _logger.LogError(ex, "State store operation failed: {StoreName}/{Command}",
                binding.StoreName, command);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType,
                metadata: BaseMetadata(binding, extra: new()
                {
                    ["ExceptionType"] = ex.GetType().Name
                }));
        }
    }

    private async Task<TaskInvocationResult> GetAsync(
        StateStoreBinding binding,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return MissingFieldFailure(binding, stopwatch, "get requires 'key'");
        }

        var consistency = ParseConsistency(binding.Consistency);
        var metadata = BuildMetadata(binding, includeTtl: false);

        var (value, etag) = await _daprClient.GetStateAndETagAsync<JsonElement>(
            binding.StoreName,
            binding.Key,
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
            metadata: BaseMetadata(binding, extra: new()
            {
                ["Found"] = found,
                ["ETag"] = etag ?? string.Empty
            }));
    }

    private async Task<TaskInvocationResult> SetAsync(
        StateStoreBinding binding,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return MissingFieldFailure(binding, stopwatch, "set requires 'key'");
        }

        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            return MissingFieldFailure(binding, stopwatch, "set requires 'value'");
        }

        var value = JsonSerializer.Deserialize<JsonElement>(binding.Value);
        var metadata = BuildMetadata(binding, includeTtl: true);
        var stateOptions = BuildStateOptions(binding);

        bool saved = true;
        if (!string.IsNullOrEmpty(binding.ETag))
        {
            saved = await _daprClient.TrySaveStateAsync(
                binding.StoreName,
                binding.Key,
                value,
                binding.ETag,
                stateOptions,
                metadata,
                cancellationToken);
        }
        else
        {
            await _daprClient.SaveStateAsync(
                binding.StoreName,
                binding.Key,
                value,
                stateOptions,
                metadata,
                cancellationToken);
        }

        stopwatch.Stop();

        return TaskInvocationResult.Success(
            data: new { Saved = saved, Key = binding.Key },
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, extra: new() { ["Saved"] = saved }));
    }

    private async Task<TaskInvocationResult> DeleteAsync(
        StateStoreBinding binding,
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
                    binding.StoreName,
                    binding.Query,
                    metadata,
                    cancellationToken);

                matchedKeys = queryResponse.Results?.Select(r => r.Key).ToList() ?? new List<string>();
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                return TaskInvocationResult.Failure(
                    error: $"Query-based deletion is not supported by state store '{binding.StoreName}': {ex.Message}",
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType,
                    metadata: BaseMetadata(binding, extra: new()
                    {
                        ["ExceptionType"] = ex.GetType().Name
                    }));
            }

            var deletedByQuery = await DeleteKeysAsync(binding, matchedKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, stopwatch, deletedByQuery);
        }

        // 2. Bulk key list deletion.
        if (binding.Keys is { Count: > 0 })
        {
            var deleted = await DeleteKeysAsync(binding, binding.Keys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, stopwatch, deleted);
        }

        // 3. Single key deletion.
        if (!string.IsNullOrWhiteSpace(binding.Key))
        {
            var stateOptions = BuildStateOptions(binding);
            await _daprClient.DeleteStateAsync(
                binding.StoreName,
                binding.Key,
                stateOptions,
                metadata,
                cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(binding, stopwatch, 1);
        }

        return MissingFieldFailure(binding, stopwatch,
            "delete requires one of 'key', 'keys' or 'query'");
    }

    private async Task<int> DeleteKeysAsync(
        StateStoreBinding binding,
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

        await _daprClient.DeleteBulkStateAsync(binding.StoreName, items, cancellationToken);
        return items.Count;
    }

    private TaskInvocationResult DeleteSuccess(
        StateStoreBinding binding,
        Stopwatch stopwatch,
        int deletedCount) =>
        TaskInvocationResult.Success(
            data: new { DeletedCount = deletedCount },
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding, extra: new() { ["DeletedCount"] = deletedCount }));

    private TaskInvocationResult UnsupportedCommand(StateStoreBinding binding, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return TaskInvocationResult.Failure(
            error: $"Unsupported state store command: '{binding.Command}'. " +
                   $"Expected one of: {GetCommand}, {SetCommand}, {DeleteCommand}.",
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding));
    }

    private TaskInvocationResult MissingFieldFailure(
        StateStoreBinding binding,
        Stopwatch stopwatch,
        string message)
    {
        stopwatch.Stop();
        return TaskInvocationResult.Failure(
            error: message,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType,
            metadata: BaseMetadata(binding));
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
        Dictionary<string, object>? extra = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["StoreName"] = binding.StoreName,
            ["Command"] = binding.Command ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(binding.Key))
        {
            metadata["Key"] = binding.Key;
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
