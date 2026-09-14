using BBT.Workflow.Caching;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Mapping;

namespace BBT.Workflow.Functions;

/// <summary>
/// Reads/writes a cached value through the Execution service's <c>statestore</c> invoker (via
/// <see cref="IRemoteInvokerService"/>), rather than talking to Dapr directly from Orchestration.
/// Used for function-level read-through caching; shares the State Store task's <c>custom:</c> key
/// prefix, TTL and consistency semantics.
/// </summary>
public interface IStateStoreCacheGateway
{
    /// <summary>Reads <paramref name="key"/>. <see cref="CacheGetResult.Hit"/> is false on a miss.</summary>
    Task<CacheGetResult> GetAsync(
        string key, string? storeName, string? consistency, TaskTraceContext traceContext, CancellationToken cancellationToken = default);

    /// <summary>Writes <paramref name="value"/> under <paramref name="key"/>. Returns whether it succeeded.</summary>
    Task<bool> SetAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency, TaskTraceContext traceContext, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of a cache read. <paramref name="CacheOk"/> is false when the cache operation failed.</summary>
public readonly record struct CacheGetResult(bool CacheOk, bool Hit, JsonElement Value);

/// <inheritdoc />
public sealed class StateStoreCacheGateway(IRemoteInvokerService remoteInvoker) : IStateStoreCacheGateway
{
    private const string TaskKey = "function-cache";

    /// <summary>Groups these spans apart from component-cache reads in the same query.</summary>
    private const string ComponentType = "function-response";

    /// <inheritdoc />
    public async Task<CacheGetResult> GetAsync(
        string key, string? storeName, string? consistency, TaskTraceContext traceContext, CancellationToken cancellationToken = default)
    {
        // The only cache in the runtime whose hit/miss was invisible. Every other cache read draws a
        // Cache.Get with cache.hit; this one goes out through the Execution service to a Dapr state
        // store, so it appeared as an Invoke.* span with nothing saying it was a cache at all — and
        // a hit here skips the function's whole task set, which makes it the branch most worth
        // seeing. The key is authored by the domain, so it is tagged and deliberately NOT in the
        // span name.
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationGet, componentType: ComponentType);
        CacheActivityHelper.SetCacheKey(activity, key);

        var envelope = BuildEnvelope("get", key, storeName, consistency, ttlInSeconds: null, value: null);
        if (!envelope.IsSuccess)
        {
            CacheActivityHelper.SetError(activity, null);
            return new CacheGetResult(CacheOk: false, Hit: false, Value: default);
        }

        var result = await remoteInvoker.InvokeAsync(
            BBT.Workflow.Execution.TaskTypes.StateStore, TaskKey, envelope.Value!, traceContext, cancellationToken);

        if (!result.IsSuccess || !result.Value!.IsSuccess)
        {
            // A cache that cannot be read is not a failed request — the caller falls through to the
            // tasks — but it is not a miss either, and reporting it as one would hide an outage
            // behind a plausible hit ratio.
            CacheActivityHelper.SetError(activity, null);
            return new CacheGetResult(CacheOk: false, Hit: false, Value: default);
        }

        // The state store invoker returns Data = value on a hit, Data = null on a miss.
        if (result.Value.Data is JsonElement value &&
            value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            CacheActivityHelper.SetCacheHit(activity, true);
            return new CacheGetResult(CacheOk: true, Hit: true, Value: value);
        }

        CacheActivityHelper.SetCacheHit(activity, false);
        return new CacheGetResult(CacheOk: true, Hit: false, Value: default);
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency, TaskTraceContext traceContext, CancellationToken cancellationToken = default)
    {
        using var activity = CacheActivityHelper.StartActivity(
            CacheActivityHelper.OperationSet, componentType: ComponentType);
        CacheActivityHelper.SetCacheKey(activity, key);

        var envelope = BuildEnvelope("set", key, storeName, consistency, ttlInSeconds, value);
        if (!envelope.IsSuccess)
        {
            CacheActivityHelper.SetError(activity, null);
            return false;
        }

        var result = await remoteInvoker.InvokeAsync(
            BBT.Workflow.Execution.TaskTypes.StateStore, TaskKey, envelope.Value!, traceContext, cancellationToken);

        var written = result.IsSuccess && result.Value!.IsSuccess;
        if (!written)
            CacheActivityHelper.SetError(activity, null);

        return written;
    }

    private static Result<TaskEnvelope> BuildEnvelope(
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

        return TaskBindingMapper.CreateEnvelope(task);
    }
}
