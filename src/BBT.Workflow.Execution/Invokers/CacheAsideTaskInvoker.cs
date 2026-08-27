using System.Diagnostics;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.StateStores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Cache-Aside (read-through) task invoker. Uses the shared <see cref="IStateStoreClient"/> for the
/// state-store get/set (same <c>custom:</c> prefix, TTL and consistency as the State Store task) and
/// adds the read-through flow: read the key, return it on a hit, otherwise dispatch the pre-resolved
/// source task envelope through the local <see cref="ITaskInvokerRegistry"/> and write its raw result
/// back to the cache. The cached value is the raw source result; any shaping mapping is applied by the
/// Orchestration-side executor on read (its scripting engine is not available here).
/// </summary>
public sealed class CacheAsideTaskInvoker : ITaskInvoker<CacheAsideBinding>
{
    private readonly IStateStoreClient _stateStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CacheAsideTaskInvoker> _logger;

    public CacheAsideTaskInvoker(
        IStateStoreClient stateStore,
        IServiceProvider serviceProvider,
        ILogger<CacheAsideTaskInvoker> logger)
    {
        _stateStore = stateStore;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public string TaskType => TaskTypes.CacheAside;

    /// <inheritdoc />
    public Type BindingType => typeof(CacheAsideBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<CacheAsideBinding> descriptor,
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
        var typedBinding = binding.Deserialize<CacheAsideBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize CacheAsideBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        CacheAsideBinding binding,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(binding.Key))
        {
            return TaskInvocationResult.Failure(
                error: "CacheAside requires a non-empty 'key'.",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType);
        }

        var storeName = _stateStore.ResolveStoreName(binding.StoreName);
        var canCache = !string.IsNullOrWhiteSpace(storeName);

        if (!canCache && !binding.BypassOnCacheError)
        {
            return TaskInvocationResult.Failure(
                error: "State store name is not configured: set 'storeName' in the task config " +
                       "or the DAPR_STATE_STORE_NAME configuration value.",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType);
        }

        // 1. Cache read (unless forceRefresh / uncacheable).
        if (canCache && !binding.ForceRefresh)
        {
            try
            {
                // The hit/miss decision is the whole point of this task type; without a span for it
                // a reader can only infer the outcome from whether a source-task span follows.
                using var readActivity = InvokerActivityHelper.StartCacheAsideActivity("Read", binding.Key);
                var entry = await _stateStore.GetAsync(
                    storeName!, binding.Key, binding.Consistency, metadata: null, cancellationToken);
                InvokerActivityHelper.SetCacheHit(readActivity, entry.Found);

                if (entry.Found)
                {
                    return TaskInvocationResult.Success(
                        data: entry.Value,
                        body: entry.Value.GetRawText(),
                        executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                        taskType: TaskType,
                        metadata: Metadata(binding, storeName!, cacheHit: true, refreshed: false, entry.ETag));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!binding.BypassOnCacheError)
                {
                    return TaskInvocationResult.Failure(
                        error: $"CacheAside read failed: {ex.Message}",
                        executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                        taskType: TaskType,
                        metadata: new Dictionary<string, object> { ["ExceptionType"] = ex.GetType().Name });
                }

                _logger.LogWarning(ex,
                    "CacheAside {TaskKey}: cache read failed. Falling back to the source task (bypassOnCacheError=true).",
                    taskKey);
            }
        }

        // 2. Cache miss / forceRefresh: run the source task on this Execution service.
        var registry = _serviceProvider.GetRequiredService<ITaskInvokerRegistry>();
        var sourceResult = await registry.InvokeAsync(binding.SourceTask, cancellationToken);
        if (!sourceResult.IsSuccess || sourceResult.Data is null)
        {
            // Propagate the source outcome unchanged (a business failure flows to the error boundary chain;
            // a null result is simply not cached).
            return sourceResult;
        }

        // 3. Best-effort cache write.
        if (canCache)
        {
            try
            {
                using var writeActivity = InvokerActivityHelper.StartCacheAsideActivity("Write", binding.Key);
                await _stateStore.SetAsync(
                    storeName!, binding.Key, sourceResult.Data, binding.TtlInSeconds, binding.Consistency,
                    concurrency: null, etag: null, metadata: null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!binding.BypassOnCacheError)
                {
                    return TaskInvocationResult.Failure(
                        error: $"CacheAside write failed: {ex.Message}",
                        executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                        taskType: TaskType,
                        metadata: new Dictionary<string, object> { ["ExceptionType"] = ex.GetType().Name });
                }

                _logger.LogWarning(ex,
                    "CacheAside {TaskKey}: cache write failed. Returning the source result anyway (bypassOnCacheError=true).",
                    taskKey);
            }
        }

        return TaskInvocationResult.Success(
            data: sourceResult.Data,
            body: sourceResult.Body,
            executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
            taskType: TaskType,
            metadata: Metadata(binding, storeName ?? string.Empty, cacheHit: false, refreshed: true, etag: null));
    }

    private Dictionary<string, object> Metadata(
        CacheAsideBinding binding,
        string storeName,
        bool cacheHit,
        bool refreshed,
        string? etag) => new()
    {
        ["StoreName"] = storeName,
        ["Key"] = _stateStore.PrefixKey(binding.Key),
        ["CacheHit"] = cacheHit,
        ["Refreshed"] = refreshed,
        ["ETag"] = etag ?? string.Empty
    };
}
