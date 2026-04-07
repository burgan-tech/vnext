using System.Text.Json;
using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Monitor.Components.DTOs;

namespace BBT.Workflow.Monitor.Components;

/// <summary>
/// Read-only query service for workflow component definitions.
/// Dispatches to <see cref="IComponentCacheStore"/> based on the requested component type.
/// </summary>
public sealed class MonitorComponentQueryService(
    IServiceProvider serviceProvider,
    IComponentCacheStore componentCacheStore)
    : ApplicationService(serviceProvider), IMonitorComponentQueryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public async Task<Result<MonitorComponentResponse>> GetComponentsAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        return input.ComponentType switch
        {
            MonitorComponentTypes.Flows => await GetSingleOrListAsync(
                input,
                (key, version, ct) => componentCacheStore.GetFlowAsync(input.Domain, key, version, ct),
                cancellationToken),

            MonitorComponentTypes.Tasks => await GetSingleOrListAsync(
                input,
                (key, version, ct) => componentCacheStore.GetTaskAsync(input.Domain, key, version, ct),
                cancellationToken),

            MonitorComponentTypes.Schemas => await GetSingleOrListAsync(
                input,
                (key, version, ct) => componentCacheStore.GetSchemaAsync(input.Domain, key, version, ct),
                cancellationToken),

            MonitorComponentTypes.Extensions => await GetExtensionsAsync(input, cancellationToken),

            MonitorComponentTypes.Functions => await GetSingleOrListAsync(
                input,
                (key, version, ct) => componentCacheStore.GetFunctionAsync(input.Domain, key, version, ct),
                cancellationToken),

            MonitorComponentTypes.Views => await GetSingleOrListAsync(
                input,
                (key, version, ct) => componentCacheStore.GetViewAsync(input.Domain, key, version, ct),
                cancellationToken),

            _ => Result<MonitorComponentResponse>.Fail(
                Error.Validation("component.unknownType",
                    $"Unknown component type '{input.ComponentType}'. " +
                    $"Supported: {string.Join(", ", MonitorComponentTypes.Flows, MonitorComponentTypes.Tasks, MonitorComponentTypes.Schemas, MonitorComponentTypes.Extensions, MonitorComponentTypes.Functions, MonitorComponentTypes.Views)}."))
        };
    }

    private async Task<Result<MonitorComponentResponse>> GetSingleOrListAsync<T>(
        MonitorGetComponentsInput input,
        Func<string, string?, CancellationToken, Task<Result<T>>> getByKey,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            var result = await getByKey(input.Key, input.Version, cancellationToken);
            if (!result.IsSuccess)
                return Result<MonitorComponentResponse>.Fail(result.Error);

            return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
            {
                ComponentType = input.ComponentType,
                Items = [Serialize(result.Value!)]
            });
        }

        return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
        {
            ComponentType = input.ComponentType,
            Items = []
        });
    }

    private async Task<Result<MonitorComponentResponse>> GetExtensionsAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            var single = await componentCacheStore.GetExtensionAsync(input.Domain, input.Key, input.Version, cancellationToken);
            if (!single.IsSuccess)
                return Result<MonitorComponentResponse>.Fail(single.Error);

            return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
            {
                ComponentType = MonitorComponentTypes.Extensions,
                Items = [Serialize(single.Value!)]
            });
        }

        var all = await componentCacheStore.GetAllExtensionsAsync(input.Domain, cancellationToken);
        if (!all.IsSuccess)
            return Result<MonitorComponentResponse>.Fail(all.Error);

        return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
        {
            ComponentType = MonitorComponentTypes.Extensions,
            Items = all.Value!.Select(Serialize).ToList()
        });
    }

    private static JsonElement Serialize<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        return JsonDocument.Parse(json).RootElement;
    }
}
