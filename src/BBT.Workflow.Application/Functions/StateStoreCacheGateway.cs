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

    /// <inheritdoc />
    public async Task<CacheGetResult> GetAsync(
        string key, string? storeName, string? consistency, TaskTraceContext traceContext, CancellationToken cancellationToken = default)
    {
        var envelope = BuildEnvelope("get", key, storeName, consistency, ttlInSeconds: null, value: null);
        if (!envelope.IsSuccess)
        {
            return new CacheGetResult(CacheOk: false, Hit: false, Value: default);
        }

        var result = await remoteInvoker.InvokeAsync(
            BBT.Workflow.Execution.TaskTypes.StateStore, TaskKey, envelope.Value!, traceContext, cancellationToken);

        if (!result.IsSuccess || !result.Value!.IsSuccess)
        {
            return new CacheGetResult(CacheOk: false, Hit: false, Value: default);
        }

        // The state store invoker returns Data = value on a hit, Data = null on a miss.
        if (result.Value.Data is JsonElement value &&
            value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            return new CacheGetResult(CacheOk: true, Hit: true, Value: value);
        }

        return new CacheGetResult(CacheOk: true, Hit: false, Value: default);
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        string key, object? value, int? ttlInSeconds, string? storeName, string? consistency, TaskTraceContext traceContext, CancellationToken cancellationToken = default)
    {
        var envelope = BuildEnvelope("set", key, storeName, consistency, ttlInSeconds, value);
        if (!envelope.IsSuccess)
        {
            return false;
        }

        var result = await remoteInvoker.InvokeAsync(
            BBT.Workflow.Execution.TaskTypes.StateStore, TaskKey, envelope.Value!, traceContext, cancellationToken);

        return result.IsSuccess && result.Value!.IsSuccess;
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
