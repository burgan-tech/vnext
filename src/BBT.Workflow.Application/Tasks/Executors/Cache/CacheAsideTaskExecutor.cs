using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Caching;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for Cache-Aside (read-through) tasks.
/// Reads the resolved key from the configured cache; on a hit it returns the cached value without
/// executing the source task. On a miss (or <see cref="CacheAsideTask.ForceRefresh"/>) it executes the
/// referenced source task, applies the optional source mapping, writes the shaped value back to the
/// cache with a TTL, and returns it. Cache infrastructure failures fall back to the source task when
/// <see cref="CacheAsideTask.BypassOnCacheError"/> is set; otherwise they surface as a task failure that
/// flows into the error boundary chain.
/// </summary>
public sealed class CacheAsideTaskExecutor : TaskExecutorBase<CacheAsideTask>
{
    private static readonly string CacheAsideTaskType = TaskType.CacheAside.ToString();

    private readonly IStateStoreAccessor _stateStore;
    private readonly IScriptEngine _scriptEngine;
    private readonly ITaskFactory _taskFactory;

    // Resolved lazily to break the DI cycle: the registry enumerates all ITaskExecutor instances,
    // which includes this executor.
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of <see cref="CacheAsideTaskExecutor"/>.
    /// </summary>
    public CacheAsideTaskExecutor(
        IStateStoreAccessor stateStore,
        IScriptEngine scriptEngine,
        ITaskFactory taskFactory,
        IServiceProvider serviceProvider,
        ILogger<CacheAsideTaskExecutor> logger)
        : base(logger)
    {
        _stateStore = stateStore;
        _scriptEngine = scriptEngine;
        _taskFactory = taskFactory;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.CacheAside;

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        CacheAsideTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. Resolve the cache key — via an optional key mapping (.csx) that computes it from the full
        //    script context, or by interpolating the static key template. A key mapping that returns
        //    null / empty signals the result is NOT cacheable for this request (e.g. the evaluation
        //    depends on database variables whose values can change any time): the source runs directly
        //    with no cache read/write.
        string? key;
        var bypassCache = false;
        if (task.KeyMapping is { } keyMapping && keyMapping.HasMappingCode)
        {
            var keyResult = await ResolveKeyViaMappingAsync(keyMapping, context, cancellationToken);
            if (!keyResult.IsSuccess)
            {
                return Fail(stopwatch, $"CacheAside key mapping failed: {keyResult.Error.Message}");
            }

            key = keyResult.Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                bypassCache = true;
                Logger.LogInformation(
                    "CacheAside task {TaskKey}: key mapping returned no key — result is not cacheable, " +
                    "running the source task without caching.",
                    task.Key);
            }
        }
        else
        {
            try
            {
                key = CacheKeyInterpolator.Interpolate(task.CacheKey, context.ScriptContext);
            }
            catch (Exception ex)
            {
                return Fail(stopwatch, $"CacheAside key could not be resolved: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                return Fail(stopwatch, "CacheAside task requires a non-empty 'key'.");
            }
        }

        var storeName = _stateStore.ResolveStoreName(task.StoreName);

        // 2. Cache read (skipped on forceRefresh or when the result is not cacheable).
        if (!task.ForceRefresh && !bypassCache)
        {
            if (string.IsNullOrWhiteSpace(storeName))
            {
                var storeError = "State store name is not configured: set 'storeName' in the task config " +
                                 "or the DAPR_STATE_STORE_NAME configuration value.";
                if (!task.BypassOnCacheError)
                {
                    return Fail(stopwatch, storeError);
                }

                Logger.LogWarning(
                    "CacheAside task {TaskKey}: {Error} Falling back to the source task (bypassOnCacheError=true).",
                    task.Key, storeError);
            }
            else
            {
                var read = await TryReadCacheAsync(task, storeName, key, cancellationToken);
                if (!read.CacheOk)
                {
                    if (!task.BypassOnCacheError)
                    {
                        return Fail(stopwatch, $"CacheAside read failed: {read.Error}");
                    }

                    Logger.LogWarning(
                        "CacheAside task {TaskKey}: cache read failed ({Error}). Falling back to the source task " +
                        "(bypassOnCacheError=true).",
                        task.Key, read.Error);
                }
                else if (read.Hit)
                {
                    stopwatch.Stop();
                    return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                        data: read.Value,
                        body: read.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                            ? null
                            : read.Value.GetRawText(),
                        executionDurationMs: stopwatch.ElapsedMilliseconds,
                        taskType: CacheAsideTaskType,
                        metadata: BuildMetadata(key, cacheHit: true, refreshed: false)));
                }
            }
        }

        // 3. Cache miss / forceRefresh: execute the source task.
        var sourceResult = await ExecuteSourceTaskAsync(task, context, cancellationToken);
        if (!sourceResult.IsSuccess)
        {
            return Fail(stopwatch, $"CacheAside source task failed: {sourceResult.Error.Message}");
        }

        var sourceResponse = sourceResult.Value!;
        if (!sourceResponse.IsSuccess)
        {
            // Propagate the source business failure so the error boundary / auto-transitions handle it.
            stopwatch.Stop();
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: sourceResponse.ErrorMessage ?? "Source task reported a business failure.",
                statusCode: sourceResponse.StatusCode,
                executionDurationMs: stopwatch.ElapsedMilliseconds,
                taskType: CacheAsideTaskType,
                metadata: BuildMetadata(key ?? string.Empty, cacheHit: false, refreshed: false)));
        }

        // 4. Shape the source result via the optional source mapping.
        var shapeResult = await ApplySourceMappingAsync(task, sourceResponse, context, cancellationToken);
        if (!shapeResult.IsSuccess)
        {
            return Fail(stopwatch, $"CacheAside source mapping failed: {shapeResult.Error.Message}");
        }

        var shaped = shapeResult.Value;

        // 5. Best-effort cache write (skipped when the result is not cacheable).
        if (!bypassCache && shaped is not null && !string.IsNullOrWhiteSpace(storeName))
        {
            var valueElement = shaped as JsonElement?
                ?? JsonSerializer.SerializeToElement(shaped, JsonSerializerConstants.JsonOptions);

            var writeError = await TryWriteCacheAsync(task, storeName, key, valueElement, cancellationToken);
            if (writeError is not null && !task.BypassOnCacheError)
            {
                return Fail(stopwatch, $"CacheAside write failed: {writeError}");
            }

            if (writeError is not null)
            {
                Logger.LogWarning(
                    "CacheAside task {TaskKey}: cache write failed ({Error}). Returning the source result anyway " +
                    "(bypassOnCacheError=true).",
                    task.Key, writeError);
            }
        }

        stopwatch.Stop();
        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
            data: shaped,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: CacheAsideTaskType,
            metadata: BuildMetadata(key ?? string.Empty, cacheHit: false, refreshed: !bypassCache)));
    }

    /// <summary>
    /// Resolves the cache key by running the optional key mapping's <c>OutputHandler</c> against the
    /// current script context. The mapping returns the key as a plain string in <c>Data</c>; a
    /// <c>null</c> / empty result signals the value is not cacheable for this request. Generic: the
    /// runtime imposes no domain semantics — the mapping alone decides the key and cacheability.
    /// </summary>
    private async Task<Result<string?>> ResolveKeyViaMappingAsync(
        ScriptCode mapping,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        return await ResultExtensions.TryAsync<string?>(async ct =>
        {
            var runner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            var output = await runner.OutputHandler(context.ScriptContext);
            return output.Data?.ToString();
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Key mapping output handler failed: {ex.Message}"));
    }

    /// <summary>
    /// Reads the cache. Returns whether the read succeeded (infrastructure-wise) and, if so, whether it hit.
    /// </summary>
    private async Task<CacheReadOutcome> TryReadCacheAsync(
        CacheAsideTask task,
        string storeName,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _stateStore.GetAsync(
                storeName, key, task.Consistency, metadata: null, cancellationToken);
            return new CacheReadOutcome(CacheOk: true, Hit: result.Found, Value: result.Value, Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CacheReadOutcome(CacheOk: false, Hit: false, Value: default, Error: ex.Message);
        }
    }

    /// <summary>
    /// Writes the shaped value to the cache. Returns null on success, or an error message on failure.
    /// </summary>
    private async Task<string?> TryWriteCacheAsync(
        CacheAsideTask task,
        string storeName,
        string key,
        JsonElement value,
        CancellationToken cancellationToken)
    {
        try
        {
            await _stateStore.SetAsync(
                storeName, key, value, task.TtlInSeconds, task.Consistency,
                concurrency: null, etag: null, metadata: null, cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Resolves and executes the source task through its own executor (without going through the retry /
    /// error-boundary engine, so the cache-aside task owns the single instance-data result).
    /// </summary>
    private async Task<Result<StandardTaskResponse>> ExecuteSourceTaskAsync(
        CacheAsideTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        if (task.SourceTask is null)
        {
            return Result<StandardTaskResponse>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                "CacheAside task requires a 'sourceTask' reference."));
        }

        var sourceTaskResult = await _taskFactory.CreateExecutionTaskAsync(task.SourceTask, cancellationToken);
        if (!sourceTaskResult.IsSuccess)
        {
            return Result<StandardTaskResponse>.Fail(sourceTaskResult.Error);
        }

        var sourceTask = sourceTaskResult.Value!;

        var registry = _serviceProvider.GetRequiredService<ITaskExecutorRegistry>();
        var executorResult = registry.GetExecutor(sourceTask.GetTaskType());
        if (!executorResult.IsSuccess)
        {
            return Result<StandardTaskResponse>.Fail(executorResult.Error);
        }

        // The source task runs with no mapping of its own; the cache-aside task applies its own
        // sourceMapping to the result afterwards.
        var sourceOnExecute = OnExecuteTask.Create(0, task.SourceTask, ScriptCode.FromNative(string.Empty));
        var sourceContext = new TaskExecutorContext(
            sourceTask, sourceOnExecute, context.ScriptContext, context.InstanceTransitionId, context.TaskTrigger);

        return await executorResult.Value!.ExecuteAsync(sourceContext, cancellationToken);
    }

    /// <summary>
    /// Applies the optional source mapping (OutputHandler) to the source response, returning the shaped data.
    /// When no mapping is configured, the raw source data is returned.
    /// </summary>
    private async Task<Result<object?>> ApplySourceMappingAsync(
        CacheAsideTask task,
        StandardTaskResponse sourceResponse,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        if (task.SourceMapping is not { } mapping || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(sourceResponse.Data);
        }

        // Expose the source response on the script context so the mapping's OutputHandler can read it.
        context.ScriptContext.SetStandardResponse(sourceResponse, task.SourceTask.Key.ToVariableName());

        return await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var runner = await _scriptEngine.CompileToInstanceAsync<IMapping>(
                mapping,
                flowScripts: context.ScriptContext.Workflow?.Scripts,
                cancellationToken: ct);

            var output = await runner.OutputHandler(context.ScriptContext);
            return output.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"Source mapping output handler failed: {ex.Message}"));
    }

    private static Dictionary<string, object> BuildMetadata(string key, bool cacheHit, bool refreshed) => new()
    {
        ["CacheHit"] = cacheHit,
        ["Refreshed"] = refreshed,
        ["Key"] = key
    };

    private static Result<TaskInvocationResult> Fail(Stopwatch stopwatch, string message)
    {
        stopwatch.Stop();
        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
            error: message,
            executionDurationMs: stopwatch.ElapsedMilliseconds,
            taskType: CacheAsideTaskType));
    }

    private readonly record struct CacheReadOutcome(bool CacheOk, bool Hit, JsonElement Value, string? Error);
}
