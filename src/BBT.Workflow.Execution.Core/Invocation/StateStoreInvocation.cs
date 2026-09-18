using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Core.StateStores;

namespace BBT.Workflow.Execution.Core.Invocation;

/// <summary>
/// The single implementation of a state-store task call, shared by both hosts: the Execution
/// service's <c>StateStoreTaskInvoker</c> and the Orchestrator's in-process path delegate here, so
/// the two cannot drift behaviorally — get/set/delete command routing and result shaping are
/// defined exactly once, composed on top of the shared <see cref="IStateStoreClient"/> (also
/// relocated to this project for the same reason: store-name resolution, the <c>custom:</c> key
/// prefix, TTL and consistency/concurrency).
/// <para>
/// Lives in <c>Execution.Core</c> because the Orchestration host cannot reference
/// <c>BBT.Workflow.Execution</c> (pythonnet, KubernetesClient, Dapr.AI, a hosted service) and the
/// Abstractions assembly stays contract-only. Mirrors <c>SoapInvocation</c> /
/// <c>DaprServiceInvocation</c>: no logging or metrics happen here — every outcome, including a
/// missing store name, cancellation and an unhandled exception, is returned as a
/// <see cref="TaskInvocationResult"/> whose metadata carries what the hosts need to log
/// (<c>Cancelled</c>, <c>ExceptionType</c>).
/// </para>
/// <para>
/// Unlike the HTTP-shaped cores (HTTP, SOAP, Dapr service invocation), a state-store result never
/// carries a numeric <see cref="TaskInvocationResult.StatusCode"/> on any path — there is no wire
/// status code to report for a Dapr state-store call. A host's "transport failure" branch
/// (<c>StatusCode is null</c>) is therefore true for every non-cancelled failure returned here,
/// not just connection-level ones (a missing store name and an unsupported command included).
/// </para>
/// </summary>
public static class StateStoreInvocation
{
    // Command constants (case-insensitive match against binding.Command).
    private const string GetCommand = "get";
    private const string SetCommand = "set";
    private const string DeleteCommand = "delete";

    /// <summary>
    /// Deliberately the SAME source name as the Execution host's <c>InvokerActivityHelper</c> and
    /// as the other extracted invocation cores (ActivitySource listeners match by name): the
    /// <c>Invoke.Prepare</c> span this core starts is the same span family the host's other
    /// invokers use, and the Execution host's <c>BBT.Workflow.Execution*</c> AdditionalSources
    /// wildcard exports it without new config. The helper itself lives in the host assembly, which
    /// this project cannot reference.
    /// </summary>
    private static readonly ActivitySource InvokerActivitySource = new("BBT.Workflow.Execution.Invokers");

    private static Activity? StartPrepareActivity(string taskType, string? taskKey)
    {
        var activity = InvokerActivitySource.StartActivity("Invoke.Prepare", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("vnext.task.key", taskKey ?? string.Empty);
            activity.SetTag("vnext.task.type", taskType);
        }
        return activity;
    }

    /// <summary>
    /// Executes the state-store command described by the binding. Never throws: store-name
    /// resolution failure, command dispatch failure, cancellation and transport exceptions all
    /// become failed results so the caller's error boundary decides.
    /// </summary>
    /// <param name="stateStore">The shared state-store gateway (store-name resolution, the
    /// <c>custom:</c> prefix, TTL/consistency/concurrency handling).</param>
    /// <param name="binding">The prepared state-store binding (command, key(s), value, options).</param>
    /// <param name="taskType">Task-type label stamped on the result (each host stamps its own).</param>
    /// <param name="cancellationToken">Caller cancellation; a fire during the operation yields a
    /// failed result with <c>Cancelled = true</c> metadata.</param>
    /// <param name="taskKey">Task key, used only for the prepare span's tag.</param>
    public static async Task<TaskInvocationResult> ExecuteAsync(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        CancellationToken cancellationToken,
        string? taskKey = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var command = binding.Command ?? string.Empty;
        var prepareActivity = StartPrepareActivity(taskType, taskKey);

        var storeName = string.Empty;

        try
        {
            // Store-name resolution is prep too — it runs inside the try so a throw here still
            // reaches the catches below and disposes prepareActivity (no un-stopped ambient span).
            storeName = stateStore.ResolveStoreName(binding.StoreName) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(storeName))
            {
                prepareActivity?.Dispose();
                return TaskInvocationResult.Failure(
                    error: "State store name is not configured: set 'storeName' in the task config " +
                           "or the DAPR_STATE_STORE_NAME configuration value",
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    metadata: BaseMetadata(stateStore, binding, storeName: string.Empty));
            }

            // Dispose immediately before dispatch: whichever command branch runs next performs
            // the actual Dapr state-store operation (the first outbound call for this invocation).
            prepareActivity?.Dispose();

            return command.ToLowerInvariant() switch
            {
                GetCommand => await GetAsync(stateStore, binding, taskType, storeName, startTimestamp, cancellationToken),
                SetCommand => await SetAsync(stateStore, binding, taskType, storeName, startTimestamp, cancellationToken),
                DeleteCommand => await DeleteAsync(stateStore, binding, taskType, storeName, startTimestamp, cancellationToken),
                _ => UnsupportedCommand(stateStore, binding, taskType, storeName, startTimestamp)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: "State store operation was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: BaseMetadata(stateStore, binding, storeName, extra: new() { ["Cancelled"] = true }));
        }
        catch (Exception ex)
        {
            prepareActivity?.Dispose();
            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: taskType,
                metadata: BaseMetadata(stateStore, binding, storeName, extra: new()
                {
                    ["ExceptionType"] = ex.GetType().Name
                }));
        }
    }

    private static async Task<TaskInvocationResult> GetAsync(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        string storeName,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return MissingFieldFailure(stateStore, binding, taskType, storeName, startTimestamp, "get requires 'key'");
        }

        var entry = await stateStore.GetAsync(
            storeName, binding.Key, binding.Consistency, binding.Metadata, cancellationToken);

        return TaskInvocationResult.Success(
            data: entry.Found ? (object?)entry.Value : null,
            body: entry.Found ? entry.Value.GetRawText() : null,
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: taskType,
            metadata: BaseMetadata(stateStore, binding, storeName, extra: new()
            {
                ["Found"] = entry.Found,
                ["ETag"] = entry.ETag ?? string.Empty
            }));
    }

    private static async Task<TaskInvocationResult> SetAsync(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        string storeName,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return MissingFieldFailure(stateStore, binding, taskType, storeName, startTimestamp, "set requires 'key'");
        }

        if (string.IsNullOrWhiteSpace(binding.Value))
        {
            return MissingFieldFailure(stateStore, binding, taskType, storeName, startTimestamp, "set requires 'value'");
        }

        var value = JsonSerializer.Deserialize<JsonElement>(binding.Value);
        var saved = await stateStore.SetAsync(
            storeName, binding.Key, value, binding.TtlInSeconds, binding.Consistency,
            binding.Concurrency, binding.ETag, binding.Metadata, cancellationToken);

        return TaskInvocationResult.Success(
            data: new { Saved = saved, Key = stateStore.PrefixKey(binding.Key) },
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: taskType,
            metadata: BaseMetadata(stateStore, binding, storeName, extra: new() { ["Saved"] = saved }));
    }

    private static async Task<TaskInvocationResult> DeleteAsync(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        string storeName,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        // 1. Tag/pattern based deletion via the Dapr state Query API.
        if (!string.IsNullOrWhiteSpace(binding.Query))
        {
            List<string> matchedKeys;
            try
            {
                matchedKeys = (await stateStore.QueryPrefixedKeysAsync(
                    storeName, binding.Query, binding.Metadata, cancellationToken)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return TaskInvocationResult.Failure(
                    error: $"Query-based deletion is not supported by state store '{storeName}': {ex.Message}",
                    executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    taskType: taskType,
                    metadata: BaseMetadata(stateStore, binding, storeName, extra: new()
                    {
                        ["ExceptionType"] = ex.GetType().Name
                    }));
            }

            var deletedByQuery = await stateStore.DeleteBulkAsync(storeName, matchedKeys, cancellationToken);
            return DeleteSuccess(stateStore, binding, taskType, storeName, startTimestamp, deletedByQuery);
        }

        // 2. Bulk key list deletion (task-supplied keys are namespaced).
        if (binding.Keys is { Count: > 0 })
        {
            var prefixedKeys = binding.Keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(stateStore.PrefixKey)
                .ToList();
            var deleted = await stateStore.DeleteBulkAsync(storeName, prefixedKeys, cancellationToken);
            return DeleteSuccess(stateStore, binding, taskType, storeName, startTimestamp, deleted);
        }

        // 3. Single key deletion.
        if (!string.IsNullOrWhiteSpace(binding.Key))
        {
            await stateStore.DeleteAsync(
                storeName, binding.Key, binding.Consistency, binding.Concurrency, binding.Metadata, cancellationToken);
            return DeleteSuccess(stateStore, binding, taskType, storeName, startTimestamp, 1);
        }

        return MissingFieldFailure(stateStore, binding, taskType, storeName, startTimestamp,
            "delete requires one of 'key', 'keys' or 'query'");
    }

    private static TaskInvocationResult DeleteSuccess(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        string storeName,
        long startTimestamp,
        int deletedCount) =>
        TaskInvocationResult.Success(
            data: new { DeletedCount = deletedCount },
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: taskType,
            metadata: BaseMetadata(stateStore, binding, storeName, extra: new() { ["DeletedCount"] = deletedCount }));

    private static TaskInvocationResult UnsupportedCommand(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        string storeName,
        long startTimestamp)
    {
        return TaskInvocationResult.Failure(
            error: $"Unsupported state store command: '{binding.Command}'. " +
                   $"Expected one of: {GetCommand}, {SetCommand}, {DeleteCommand}.",
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: taskType,
            metadata: BaseMetadata(stateStore, binding, storeName));
    }

    private static TaskInvocationResult MissingFieldFailure(
        IStateStoreClient stateStore,
        StateStoreBinding binding,
        string taskType,
        string storeName,
        long startTimestamp,
        string message)
    {
        return TaskInvocationResult.Failure(
            error: message,
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: taskType,
            metadata: BaseMetadata(stateStore, binding, storeName));
    }

    private static Dictionary<string, object> BaseMetadata(
        IStateStoreClient stateStore,
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
            metadata["Key"] = stateStore.PrefixKey(binding.Key);
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
