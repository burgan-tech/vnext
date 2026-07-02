using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for State Store tasks.
/// Executes locally in the Application layer — does not route through the Execution service.
/// Performs Dapr state store operations (get / set / delete) directly through DaprClient
/// to avoid the extra Orchestration → Execution hop for this latency-sensitive caching primitive.
/// </summary>
public sealed class StateStoreTaskExecutor : TaskExecutorBase<StateStoreTask>
{
    private const string GetCommand = "get";
    private const string SetCommand = "set";
    private const string DeleteCommand = "delete";

    private readonly DaprClient _daprClient;
    private readonly IScriptEngine _scriptEngine;

    /// <summary>
    /// Initializes a new instance of StateStoreTaskExecutor.
    /// </summary>
    public StateStoreTaskExecutor(
        DaprClient daprClient,
        IScriptEngine scriptEngine,
        ILogger<StateStoreTaskExecutor> logger)
        : base(logger)
    {
        _daprClient = daprClient;
        _scriptEngine = scriptEngine;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.StateStore;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        StateStoreTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<ScriptResponse?>.Ok(null);
        }

        var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            return await scriptRunner.InputHandler(task, context.ScriptContext);
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"StateStore task input handler failed: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskInputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        StateStoreTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var command = task.Command;

        try
        {
            var result = command.ToLowerInvariant() switch
            {
                GetCommand => await GetAsync(task, stopwatch, cancellationToken),
                SetCommand => await SetAsync(task, stopwatch, cancellationToken),
                DeleteCommand => await DeleteAsync(task, stopwatch, cancellationToken),
                _ => UnsupportedCommand(task, stopwatch)
            };

            if (!result.IsSuccess)
            {
                Logger.StateStoreOperationFailed(
                    task.Key,
                    task.StoreName,
                    command,
                    result.ErrorMessage ?? "Unknown error");
            }

            return Result<TaskInvocationResult>.Ok(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            Logger.StateStoreOperationCancelled(task.Key, task.StoreName, command);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: "State store operation was cancelled",
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType.ToString(),
                metadata: BaseMetadata(task, extra: new() { ["Cancelled"] = true })));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            Logger.StateStoreOperationFailed(task.Key, task.StoreName, command, ex.Message);

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: TaskType.ToString(),
                metadata: BaseMetadata(task, extra: new()
                {
                    ["ExceptionType"] = ex.GetType().Name
                })));
        }
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        StateStoreTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext);

        var result = await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var scriptRunner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            var outputResponse = await scriptRunner.OutputHandler(context.ScriptContext);
            return outputResponse.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"StateStore task output handler failed: {ex.Message}"));

        if (!result.IsSuccess)
        {
            Logger.TaskOutputHandlerFailed(
                task.Key,
                TaskType.ToString(),
                context.ScriptContext.Instance?.Id ?? Guid.Empty,
                result.Error.Message ?? "Unknown error");
        }

        return result;
    }

    private async Task<TaskInvocationResult> GetAsync(
        StateStoreTask task,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.CacheKey))
        {
            return MissingFieldFailure(task, stopwatch, "get requires 'key'");
        }

        var consistency = ParseConsistency(task.Consistency);
        var metadata = BuildMetadata(task, includeTtl: false);

        var (value, etag) = await _daprClient.GetStateAndETagAsync<JsonElement>(
            task.StoreName,
            task.CacheKey,
            consistency,
            metadata,
            cancellationToken);

        stopwatch.Stop();

        var found = value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

        return TaskInvocationResult.Success(
            data: found ? (object?)value : null,
            body: found ? value.GetRawText() : null,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType.ToString(),
            metadata: BaseMetadata(task, extra: new()
            {
                ["Found"] = found,
                ["ETag"] = etag ?? string.Empty
            }));
    }

    private async Task<TaskInvocationResult> SetAsync(
        StateStoreTask task,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.CacheKey))
        {
            return MissingFieldFailure(task, stopwatch, "set requires 'key'");
        }

        if (task.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return MissingFieldFailure(task, stopwatch, "set requires 'value'");
        }

        var metadata = BuildMetadata(task, includeTtl: true);
        var stateOptions = BuildStateOptions(task);

        bool saved = true;
        if (!string.IsNullOrEmpty(task.ETag))
        {
            saved = await _daprClient.TrySaveStateAsync(
                task.StoreName,
                task.CacheKey,
                task.Value,
                task.ETag,
                stateOptions,
                metadata,
                cancellationToken);
        }
        else
        {
            await _daprClient.SaveStateAsync(
                task.StoreName,
                task.CacheKey,
                task.Value,
                stateOptions,
                metadata,
                cancellationToken);
        }

        stopwatch.Stop();

        return TaskInvocationResult.Success(
            data: new { Saved = saved, Key = task.CacheKey },
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType.ToString(),
            metadata: BaseMetadata(task, extra: new() { ["Saved"] = saved }));
    }

    private async Task<TaskInvocationResult> DeleteAsync(
        StateStoreTask task,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var metadata = BuildMetadata(task, includeTtl: false);

        // 1. Tag/pattern based deletion via the Dapr state Query API.
        if (task.Query.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            List<string> matchedKeys;
            try
            {
                var queryResponse = await _daprClient.QueryStateAsync<JsonElement>(
                    task.StoreName,
                    task.Query.GetRawText(),
                    metadata,
                    cancellationToken);

                matchedKeys = queryResponse.Results?.Select(r => r.Key).ToList() ?? new List<string>();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                stopwatch.Stop();
                return TaskInvocationResult.Failure(
                    error: $"Query-based deletion is not supported by state store '{task.StoreName}': {ex.Message}",
                    executionDurationMs: stopwatch.ElapsedMilliseconds,
                    taskType: TaskType.ToString(),
                    metadata: BaseMetadata(task, extra: new()
                    {
                        ["ExceptionType"] = ex.GetType().Name
                    }));
            }

            var deletedByQuery = await DeleteKeysAsync(task, matchedKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(task, stopwatch, deletedByQuery);
        }

        // 2. Bulk key list deletion.
        if (task.CacheKeys is { Count: > 0 })
        {
            var deleted = await DeleteKeysAsync(task, task.CacheKeys, cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(task, stopwatch, deleted);
        }

        // 3. Single key deletion.
        if (!string.IsNullOrWhiteSpace(task.CacheKey))
        {
            var stateOptions = BuildStateOptions(task);
            await _daprClient.DeleteStateAsync(
                task.StoreName,
                task.CacheKey,
                stateOptions,
                metadata,
                cancellationToken);
            stopwatch.Stop();
            return DeleteSuccess(task, stopwatch, 1);
        }

        return MissingFieldFailure(task, stopwatch,
            "delete requires one of 'key', 'keys' or 'query'");
    }

    private async Task<int> DeleteKeysAsync(
        StateStoreTask task,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        var items = keys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => new BulkDeleteStateItem(k, etag: string.Empty))
            .ToList();

        if (items.Count == 0)
        {
            return 0;
        }

        await _daprClient.DeleteBulkStateAsync(task.StoreName, items, cancellationToken);
        return items.Count;
    }

    private TaskInvocationResult DeleteSuccess(
        StateStoreTask task,
        Stopwatch stopwatch,
        int deletedCount) =>
        TaskInvocationResult.Success(
            data: new { DeletedCount = deletedCount },
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType.ToString(),
            metadata: BaseMetadata(task, extra: new() { ["DeletedCount"] = deletedCount }));

    private TaskInvocationResult UnsupportedCommand(StateStoreTask task, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return TaskInvocationResult.Failure(
            error: $"Unsupported state store command: '{task.Command}'. " +
                   $"Expected one of: {GetCommand}, {SetCommand}, {DeleteCommand}.",
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType.ToString(),
            metadata: BaseMetadata(task));
    }

    private TaskInvocationResult MissingFieldFailure(
        StateStoreTask task,
        Stopwatch stopwatch,
        string message)
    {
        stopwatch.Stop();
        return TaskInvocationResult.Failure(
            error: message,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: TaskType.ToString(),
            metadata: BaseMetadata(task));
    }

    /// <summary>
    /// Builds the Dapr operation metadata, merging any task-supplied metadata and,
    /// when requested, the <c>ttlInSeconds</c> entry.
    /// </summary>
    private static Dictionary<string, string>? BuildMetadata(StateStoreTask task, bool includeTtl)
    {
        Dictionary<string, string>? metadata = null;
        if (task.Metadata.ValueKind == JsonValueKind.Object)
        {
            metadata = task.Metadata.Deserialize<Dictionary<string, string>>();
        }

        if (includeTtl && task.TtlInSeconds is { } ttl)
        {
            metadata ??= new Dictionary<string, string>();
            metadata["ttlInSeconds"] = ttl.ToString();
        }

        return metadata;
    }

    private static StateOptions? BuildStateOptions(StateStoreTask task)
    {
        var concurrency = ParseConcurrency(task.Concurrency);
        var consistency = ParseConsistency(task.Consistency);

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
        StateStoreTask task,
        Dictionary<string, object>? extra = null)
    {
        var metadata = new Dictionary<string, object>
        {
            ["StoreName"] = task.StoreName,
            ["Command"] = task.Command
        };

        if (!string.IsNullOrWhiteSpace(task.CacheKey))
        {
            metadata["Key"] = task.CacheKey;
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
