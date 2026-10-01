using System.Diagnostics;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Executor for Cache-Aside (read-through) tasks (type 18).
/// <para>
/// Input stage: the task-level mapping's <c>InputHandler</c> may set the key
/// (<c>task.SetCacheKey(...)</c>, like the State Store task); a <c>key</c> script (object form) is
/// then evaluated through <see cref="ICacheKeyEvaluator"/> and, when it yields a non-blank value,
/// overrides it.
/// </para>
/// <para>
/// Invoke stage: the cache get/set goes through <see cref="IStateStoreCacheGateway"/> — the same
/// gateway the function response cache uses — so cache I/O follows the host's <c>statestore</c>
/// routing mode (Local in-process or Remote through the Execution service). On a miss (or with
/// <c>forceRefresh</c>) the source runs as a task through its OWN <see cref="ITaskExecutor"/>, with
/// <c>sourceMapping</c> as its mapping, so it runs and routes exactly as it would in
/// <c>onExecutionTasks</c>; its shaped output is what gets cached. The source deliberately does NOT
/// go through <c>ITaskExecutionEngine</c>: the engine compiles the state/workflow error boundary for
/// every task it runs, so that would apply the boundary twice (once to the source, once to this
/// task) and add a second journal row. A direct executor call keeps Input → Invoke → Output with the
/// boundary applied once, on the CacheAside task.
/// </para>
/// <para>
/// Output stage: the task-level mapping's <c>OutputHandler</c> runs on the result (cached or fresh)
/// like any other task.
/// </para>
/// </summary>
public sealed class CacheAsideTaskExecutor : TaskExecutorBase<CacheAsideTask>
{
    /// <summary>Component-type tag on the gateway's <c>Cache.Get</c>/<c>Cache.Set</c> spans.</summary>
    private const string CacheComponentType = "cacheaside";

    private readonly IScriptEngine _scriptEngine;
    private readonly ITaskFactory _taskFactory;
    private readonly ICacheKeyEvaluator _keyEvaluator;
    private readonly IStateStoreCacheGateway _cacheGateway;
    private readonly IRemoteInvokerService _remoteInvoker;
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of <see cref="CacheAsideTaskExecutor"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ITaskExecutorRegistry"/> is resolved lazily from <paramref name="serviceProvider"/>:
    /// it enumerates every <see cref="ITaskExecutor"/>, this one included, so constructor injection
    /// would be a DI cycle. <paramref name="remoteInvoker"/> is used only to build the trace context.
    /// </remarks>
    public CacheAsideTaskExecutor(
        IScriptEngine scriptEngine,
        ITaskFactory taskFactory,
        ICacheKeyEvaluator keyEvaluator,
        IStateStoreCacheGateway cacheGateway,
        IRemoteInvokerService remoteInvoker,
        IServiceProvider serviceProvider,
        ILogger<CacheAsideTaskExecutor> logger)
        : base(logger)
    {
        _scriptEngine = scriptEngine;
        _taskFactory = taskFactory;
        _keyEvaluator = keyEvaluator;
        _cacheGateway = cacheGateway;
        _remoteInvoker = remoteInvoker;
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc />
    public override TaskType TaskType => TaskType.CacheAside;

    /// <inheritdoc />
    protected override async Task<Result<ScriptResponse?>> PrepareInputAsync(
        CacheAsideTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        // 1. Standard input mapping (InputHandler) — may set the cache key, like the State Store task.
        ScriptResponse? inputResponse = null;
        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is not null && mapping.HasMappingCode)
        {
            var result = await ResultExtensions.TryAsync<ScriptResponse?>(async ct =>
            {
                var scriptRunner = await GetOrCompileMappingAsync<IMapping>(_scriptEngine, context, ct);

                return await scriptRunner.InputHandler(task, context.ScriptContext);
            }, cancellationToken, ex => Error.Failure(
                WorkflowErrorCodes.TaskExecution,
                $"CacheAside task input handler failed: {ScriptDiagnostics.Explain(ex)}"));

            if (!result.IsSuccess)
            {
                return result;
            }

            inputResponse = result.Value;
        }

        // 2. A key script (object form of 'key') computes the key and overrides it when non-blank.
        if (task.KeyScript is { HasMappingCode: true } keyScript)
        {
            var keyResult = await _keyEvaluator.EvaluateAsync(keyScript, context.ScriptContext, cancellationToken);
            if (!keyResult.IsSuccess)
            {
                return Result<ScriptResponse?>.Fail(keyResult.Error);
            }

            if (!string.IsNullOrWhiteSpace(keyResult.Value))
            {
                task.SetCacheKey(keyResult.Value);
            }
        }

        return Result<ScriptResponse?>.Ok(inputResponse);
    }

    /// <inheritdoc />
    protected override async Task<Result<TaskInvocationResult>> InvokeAsync(
        CacheAsideTask task,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var taskType = TaskType.ToString();

        if (task.SourceTask is null)
        {
            return Result<TaskInvocationResult>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution, "CacheAside task requires a 'sourceTask' reference."));
        }

        if (string.IsNullOrWhiteSpace(task.CacheKey))
        {
            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                error: "CacheAside requires a non-empty 'key'.", executionDurationMs: Elapsed(), taskType: taskType));
        }

        var sourceTaskResult = await _taskFactory.CreateExecutionTaskAsync(task.SourceTask, cancellationToken);
        if (!sourceTaskResult.IsSuccess)
        {
            return Result<TaskInvocationResult>.Fail(sourceTaskResult.Error);
        }

        var sourceTask = sourceTaskResult.Value!;
        if (sourceTask is CacheAsideTask)
        {
            Logger.CacheAsideSourceTypeRejected(task.Key, sourceTask.Key);
            return Result<TaskInvocationResult>.Fail(Error.Validation(
                WorkflowErrorCodes.TaskExecution,
                $"CacheAside task '{task.Key}': the source task '{sourceTask.Key}' cannot be a CacheAside task."));
        }

        var storeName = string.IsNullOrWhiteSpace(task.StoreName) ? null : task.StoreName;
        var trace = _remoteInvoker.CreateTraceContext(context.ScriptContext);

        // 1. Read (cache I/O follows the 'statestore' routing mode, like the StateStore task and the function cache).
        if (!task.ForceRefresh)
        {
            var read = await _cacheGateway.GetAsync(
                task.CacheKey, storeName, task.Consistency, trace, cancellationToken, CacheComponentType);

            if (read is { CacheOk: true, Hit: true })
            {
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                    data: read.Value,
                    body: read.Value.GetRawText(),
                    executionDurationMs: Elapsed(),
                    taskType: taskType,
                    metadata: BuildMetadata(task.CacheKey, read.Metadata, cacheHit: true, refreshed: false)));
            }

            if (!read.CacheOk)
            {
                // A caller cancellation is not a cache failure: surface it instead of running the source.
                cancellationToken.ThrowIfCancellationRequested();

                if (!task.BypassOnCacheError)
                {
                    return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                        error: $"CacheAside read failed: {read.Error}", executionDurationMs: Elapsed(), taskType: taskType));
                }

                Logger.CacheAsideBypassedCacheError(task.Key, "read", read.Error);
            }
        }

        // 2. Miss / forceRefresh: run the source as a task.
        var source = await RunSourceAsTaskAsync(task, sourceTask, context, cancellationToken);
        if (!source.IsSuccess)
        {
            return Result<TaskInvocationResult>.Ok(source);
        }

        if (source.Data is null)
        {
            // Nothing to cache, but the result still reports what happened to the cache.
            var metadata = BuildMetadata(task.CacheKey, null, cacheHit: false, refreshed: false);
            if (source.Metadata is not null)
            {
                foreach (var (name, value) in source.Metadata)
                {
                    metadata.TryAdd(name, value);
                }
            }

            return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
                data: null,
                body: source.Body,
                statusCode: source.StatusCode ?? 200,
                executionDurationMs: Elapsed(),
                taskType: taskType,
                metadata: metadata));
        }

        // 3. Best-effort write of the shaped value.
        var write = await _cacheGateway.SetWithResultAsync(
            task.CacheKey, source.Data, task.TtlInSeconds, storeName, task.Consistency, trace, cancellationToken,
            CacheComponentType);

        if (!write.Written)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!task.BypassOnCacheError)
            {
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Failure(
                    error: $"CacheAside write failed: {write.Error}", executionDurationMs: Elapsed(), taskType: taskType));
            }

            Logger.CacheAsideBypassedCacheError(task.Key, "write", write.Error);
        }

        return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success(
            data: source.Data,
            body: source.Body,
            statusCode: source.StatusCode ?? 200,
            executionDurationMs: Elapsed(),
            taskType: taskType,
            metadata: BuildMetadata(task.CacheKey, write.Metadata, cacheHit: false, refreshed: true)));
    }

    /// <inheritdoc />
    protected override async Task<Result<object?>> ProcessOutputAsync(
        CacheAsideTask task,
        TaskInvocationResult invocationResult,
        TaskExecutorContext context,
        CancellationToken cancellationToken)
    {
        UpdateScriptContextWithResponse(task.Key, invocationResult, context.ScriptContext, context.ResponseVariableKey);

        var mapping = context.OnExecuteTask.Mapping;
        if (mapping is null || !mapping.HasMappingCode)
        {
            return Result<object?>.Ok(invocationResult.Data);
        }

        return await ResultExtensions.TryAsync<object?>(async ct =>
        {
            var runner = await GetOrCompileMappingAsync<IMapping>(_scriptEngine, context, ct);
            var output = await runner.OutputHandler(context.ScriptContext);
            return output.Data;
        }, cancellationToken, ex => Error.Failure(
            WorkflowErrorCodes.TaskExecution,
            $"CacheAside task output handler failed: {ScriptDiagnostics.Explain(ex)}"));
    }

    /// <summary>
    /// Runs the source exactly like a task in <c>onExecutionTasks</c>: its own executor, with
    /// <c>sourceMapping</c> as its mapping (InputHandler → invoke → OutputHandler). The executor routes the
    /// call itself (local / Execution service / in-process gateway / discovery), so the source runs where it
    /// would run anywhere else. Runs on a discarded parallel branch of the script context; no error boundary
    /// and no journal row of its own — the outcome flows back as this task's invocation result and the
    /// boundary applies once, on the CacheAside task.
    /// </summary>
    private async Task<TaskInvocationResult> RunSourceAsTaskAsync(
        CacheAsideTask task, WorkflowTask sourceTask, TaskExecutorContext context, CancellationToken cancellationToken)
    {
        var executorResult = _serviceProvider.GetRequiredService<ITaskExecutorRegistry>()
            .GetExecutor(sourceTask.GetTaskType());
        if (!executorResult.IsSuccess)
        {
            return TaskInvocationResult.Failure(
                error: executorResult.Error.Message ?? "No executor for the source task type.",
                taskType: TaskType.ToString());
        }

        var sourceOnExecute = OnExecuteTask.Create(
            context.OnExecuteTask.Order,
            task.SourceTask,
            task.SourceMapping ?? ScriptCode.FromNative(string.Empty));

        // Discarded, never merged back and not disposed — same convention as FanOutTaskExecutor's
        // per-item branch: the source's own response entries must not leak into the outer context;
        // only this task's result (set by ProcessOutputAsync) does.
        var sourceContext = new TaskExecutorContext(
            sourceTask, sourceOnExecute, context.ScriptContext.CreateParallelBranch(),
            context.InstanceTransitionId, context.TaskTrigger, context.Origin);

        var result = await executorResult.Value!.ExecuteAsync(sourceContext, cancellationToken);
        if (!result.IsSuccess)
        {
            return TaskInvocationResult.Failure(
                error: result.Error.Message ?? "CacheAside source task failed.", taskType: TaskType.ToString());
        }

        var response = result.Value!;
        if (!response.IsSuccess)
        {
            return TaskInvocationResult.Failure(
                error: response.ErrorMessage ?? "CacheAside source task failed.",
                statusCode: response.StatusCode,
                taskType: response.TaskType,
                metadata: response.Metadata);
        }

        JsonElement? data = response.Data is null
            ? null
            : JsonSerializer.SerializeToElement((object)response.Data, JsonSerializerConstants.JsonOptions);

        // Same Body a cache hit exposes: the raw JSON text of the (cached) data.
        return TaskInvocationResult.Success(
            data: data,
            body: data?.GetRawText(),
            statusCode: response.StatusCode ?? 200,
            taskType: response.TaskType,
            metadata: response.Metadata);
    }

    private static Dictionary<string, object> BuildMetadata(
        string key, IReadOnlyDictionary<string, object>? storeMetadata, bool cacheHit, bool refreshed) => new()
    {
        ["StoreName"] = storeMetadata?.GetValueOrDefault("StoreName") ?? string.Empty,
        ["Key"] = storeMetadata?.GetValueOrDefault("Key") ?? key,
        ["CacheHit"] = cacheHit,
        ["Refreshed"] = refreshed,
        ["ETag"] = storeMetadata?.GetValueOrDefault("ETag") ?? string.Empty
    };
}
