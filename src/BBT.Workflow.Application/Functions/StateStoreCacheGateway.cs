using BBT.Workflow.Caching;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Mapping;

namespace BBT.Workflow.Functions;

/// <summary>
/// Reads/writes a cached value through the <c>statestore</c> task invocation path (via
/// <see cref="ITaskInvocationDispatcher"/>), so every read and write follows the host's
/// <c>statestore</c> routing mode (Local in-process, or Remote through the Execution service).
/// Shared by the function response cache and the CacheAside task (type 18); both use the State
/// Store task's <c>custom:</c> key prefix, TTL and consistency semantics.
/// </summary>
public interface IStateStoreCacheGateway
{
    /// <summary>
    /// Reads <paramref name="key"/>. <see cref="CacheGetResult.Hit"/> is false on a miss;
    /// <see cref="CacheGetResult.CacheOk"/> is false when the read itself failed.
    /// </summary>
    /// <param name="key">The cache key (the state store adds its <c>custom:</c> prefix).</param>
    /// <param name="storeName">The Dapr state store; null uses the default store.</param>
    /// <param name="consistency">Optional read consistency (<c>Eventual</c>/<c>Strong</c>).</param>
    /// <param name="traceContext">Trace context propagated to the dispatched invocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="componentType">
    /// Value for the <c>Cache.Get</c> span's component-type tag; null means <c>function-response</c>.
    /// </param>
    Task<CacheGetResult> GetAsync(
        string key, string? storeName, string? consistency, TaskTraceContext traceContext,
        CancellationToken cancellationToken = default, string? componentType = null);

    /// <summary>Writes <paramref name="value"/> under <paramref name="key"/>. Returns whether it succeeded.</summary>
    Task<bool> SetAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency,
        TaskTraceContext traceContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes <paramref name="value"/> under <paramref name="key"/> and reports the outcome with the
    /// store's error message and metadata.
    /// </summary>
    /// <param name="key">The cache key (the state store adds its <c>custom:</c> prefix).</param>
    /// <param name="value">The value to cache; serialized with the central JSON options.</param>
    /// <param name="ttlInSeconds">Optional TTL in seconds.</param>
    /// <param name="storeName">The Dapr state store; null uses the default store.</param>
    /// <param name="consistency">Optional write consistency.</param>
    /// <param name="traceContext">Trace context propagated to the dispatched invocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="componentType">
    /// Value for the <c>Cache.Set</c> span's component-type tag; null means <c>function-response</c>.
    /// </param>
    Task<CacheSetResult> SetWithResultAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency,
        TaskTraceContext traceContext, CancellationToken cancellationToken = default, string? componentType = null);
}

/// <summary>Outcome of a cache read.</summary>
/// <param name="CacheOk">False when the cache operation itself failed (not a miss).</param>
/// <param name="Hit">True when a value was found.</param>
/// <param name="Value">The cached value on a hit; <c>default</c> otherwise.</param>
/// <param name="Error">The store's error message when <paramref name="CacheOk"/> is false.</param>
/// <param name="Metadata">
/// The dispatched <c>statestore</c> invocation's metadata (<c>StoreName</c>, <c>custom:</c>-prefixed
/// <c>Key</c>, <c>Found</c>, <c>ETag</c>), when the store returned any.
/// </param>
public readonly record struct CacheGetResult(
    bool CacheOk, bool Hit, JsonElement Value,
    string? Error = null,
    IReadOnlyDictionary<string, object>? Metadata = null);

/// <summary>Outcome of a cache write.</summary>
/// <param name="Written">True when the value was stored.</param>
/// <param name="Error">The store's error message when <paramref name="Written"/> is false.</param>
/// <param name="Metadata">The dispatched <c>statestore</c> invocation's metadata, when present.</param>
public readonly record struct CacheSetResult(
    bool Written, string? Error = null, IReadOnlyDictionary<string, object>? Metadata = null);

/// <inheritdoc />
public sealed class StateStoreCacheGateway(ITaskInvocationDispatcher dispatcher) : IStateStoreCacheGateway
{
    private const string TaskKey = "function-cache";

    /// <summary>Groups these spans apart from component-cache reads in the same query.</summary>
    private const string ComponentType = "function-response";

    /// <inheritdoc />
    public async Task<CacheGetResult> GetAsync(
        string key, string? storeName, string? consistency, TaskTraceContext traceContext,
        CancellationToken cancellationToken = default, string? componentType = null)
    {
        // The only cache in the runtime whose hit/miss was invisible. Every other cache read draws a
        // Cache.Get with cache.hit; this one goes out through the Execution service to a Dapr state
        // store, so it appeared as an Invoke.* span with nothing saying it was a cache at all — and
        // a hit here skips the function's whole task set, which makes it the branch most worth
        // seeing. The key is authored by the domain, so it is tagged and deliberately NOT in the
        // span name.
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, componentType: componentType ?? ComponentType);
        CacheActivityHelper.SetCacheKey(activity, key);

        var built = BuildEnvelope("get", key, storeName, consistency, ttlInSeconds: null, value: null);
        if (!built.IsSuccess)
        {
            activity.SetResultError(built.Error.Code, built.Error.Message);
            return new CacheGetResult(CacheOk: false, Hit: false, Value: default, Error: built.Error.Message);
        }

        var result = await DispatchAsync(built.Value!.Task, built.Value!.Envelope, traceContext, cancellationToken);

        if (!result.IsSuccess || !result.Value!.IsSuccess)
        {
            // A cache that cannot be read is not a failed request — the caller falls through to the
            // tasks — but it is not a miss either, and reporting it as one would hide an outage
            // behind a plausible hit ratio.
            var readError = result.IsSuccess ? result.Value!.ErrorMessage : result.Error.Message;
            activity.SetResultError(result.IsSuccess ? null : result.Error.Code, readError);
            return new CacheGetResult(
                CacheOk: false, Hit: false, Value: default,
                Error: readError,
                Metadata: result.IsSuccess ? result.Value!.Metadata : null);
        }

        // The state store invoker returns Data = value on a hit, Data = null on a miss.
        if (result.Value.Data is JsonElement value &&
            value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            return new CacheGetResult(CacheOk: true, Hit: true, Value: value, Metadata: result.Value.Metadata);
        }

        CacheActivityHelper.SetCacheHit(activity, false);
        return new CacheGetResult(CacheOk: true, Hit: false, Value: default, Metadata: result.Value.Metadata);
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency,
        TaskTraceContext traceContext, CancellationToken cancellationToken = default)
        => (await SetWithResultAsync(
            key, value, ttlInSeconds, storeName, consistency, traceContext, cancellationToken)).Written;

    /// <inheritdoc />
    public async Task<CacheSetResult> SetWithResultAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency,
        TaskTraceContext traceContext, CancellationToken cancellationToken = default, string? componentType = null)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, componentType: componentType ?? ComponentType);
        CacheActivityHelper.SetCacheKey(activity, key);

        var built = BuildEnvelope("set", key, storeName, consistency, ttlInSeconds, value);
        if (!built.IsSuccess)
        {
            activity.SetResultError(built.Error.Code, built.Error.Message);
            return new CacheSetResult(Written: false, Error: built.Error.Message);
        }

        var result = await DispatchAsync(built.Value!.Task, built.Value!.Envelope, traceContext, cancellationToken);

        if (result.IsSuccess && result.Value!.IsSuccess)
        {
            return new CacheSetResult(Written: true, Metadata: result.Value.Metadata);
        }

        var writeError = result.IsSuccess ? result.Value!.ErrorMessage : result.Error.Message;
        activity.SetResultError(result.IsSuccess ? null : result.Error.Code, writeError);
        return new CacheSetResult(
            Written: false,
            Error: writeError,
            Metadata: result.IsSuccess ? result.Value!.Metadata : null);
    }

    /// <summary>
    /// Dispatched exactly like a real state-store task, so the function cache follows the host's
    /// statestore routing instead of hard-wiring the Execution hop — the CacheAside task's cache
    /// I/O takes the same path. The task is synthetic (key "function-cache", sys reference), so the
    /// router's per-task-definition arm can never match here — per-function control belongs on the
    /// function's own cache config, a later phase.
    /// </summary>
    private Task<Result<TaskInvocationResult>> DispatchAsync(
        StateStoreTask task, TaskEnvelope envelope, TaskTraceContext traceContext, CancellationToken cancellationToken)
    {
        return dispatcher.DispatchAsync(
            task, BBT.Workflow.Execution.TaskTypes.StateStore, envelope, traceContext, cancellationToken);
    }

    private static Result<(StateStoreTask Task, TaskEnvelope Envelope)> BuildEnvelope(
        string command, string key, string? storeName, string? consistency, int? ttlInSeconds, object? value)
    {
        var config = new Dictionary<string, object?>
        {
            ["command"] = command,
            ["key"] = key
        };

        if (!string.IsNullOrWhiteSpace(storeName))
        {
            config["storeName"] = storeName;
        }

        if (!string.IsNullOrWhiteSpace(consistency))
        {
            config["consistency"] = consistency;
        }

        if (command == "set")
        {
            if (ttlInSeconds is { } ttl)
            {
                config["ttlInSeconds"] = ttl;
            }

            // Pre-serialize the value with the central options so the cached JSON matches the
            // miss-path (controller) response casing exactly.
            config["value"] = value is null
                ? null
                : JsonSerializer.SerializeToElement(value, JsonSerializerConstants.JsonOptions);
        }

        var configElement = JsonSerializer.SerializeToElement(config);
        var task = StateStoreTask.Create(configElement);
        task.SetReference(new Reference(TaskKey, "sys", "sys-tasks", "1.0.0"));

        return TaskBindingMapper.CreateEnvelope(task).Map(envelope => (task, envelope));
    }
}
