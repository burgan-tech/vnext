using System.Text.Json;
using BBT.Aether.Application.Services;
using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitor.Components.DTOs;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Monitor.Components;

/// <summary>
/// Read-only query service for workflow component definitions.
/// Resolution follows the vNext caching strategy: in-memory snapshot (<see cref="IDomainCacheContext"/>),
/// distributed + backend hydration via <see cref="IComponentCacheStore"/> / <see cref="IRuntimeService"/>.
/// Full-list DB loads are performed in an isolated DI scope (same pattern as <see cref="RuntimeCacheInitializer"/> and RuntimeCacheBackend).
/// so the request-scoped <see cref="ICurrentSchema"/> cannot leak the wrong PostgreSQL schema into definition queries.
/// </summary>
public sealed class MonitorComponentQueryService(
    IServiceProvider serviceProvider,
    IComponentCacheStore componentCacheStore,
    IDomainCacheContext domainCacheContext,
    IServiceScopeFactory serviceScopeFactory,
    IRuntimeInfoProvider runtimeInfoProvider)
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
        var canonicalType = NormalizeComponentType(input.ComponentType);
        if (canonicalType is null)
        {
            return Result<MonitorComponentResponse>.Fail(
                Error.Validation("component.unknownType",
                    $"Unknown component type '{input.ComponentType}'. " +
                    $"Supported: {string.Join(", ", MonitorComponentTypes.Flows, MonitorComponentTypes.Tasks, MonitorComponentTypes.Schemas, MonitorComponentTypes.Extensions, MonitorComponentTypes.Functions, MonitorComponentTypes.Views)}."));
        }

        return canonicalType switch
        {
            MonitorComponentTypes.Flows => await ResolveAsync<Workflow>(
                input,
                canonicalType,
                (d, k, v, ct) => componentCacheStore.GetFlowAsync(d, k, v, ct),
                ct => domainCacheContext.Workflows.GetAllByDomainAsync(input.Domain, ct),
                cancellationToken),

            MonitorComponentTypes.Tasks => await ResolveAsync<WorkflowTask>(
                input,
                canonicalType,
                (d, k, v, ct) => componentCacheStore.GetTaskAsync(d, k, v, ct),
                ct => domainCacheContext.Tasks.GetAllByDomainAsync(input.Domain, ct),
                cancellationToken),

            MonitorComponentTypes.Schemas => await ResolveAsync<SchemaDefinition>(
                input,
                canonicalType,
                (d, k, v, ct) => componentCacheStore.GetSchemaAsync(d, k, v, ct),
                ct => domainCacheContext.Schemas.GetAllByDomainAsync(input.Domain, ct),
                cancellationToken),

            MonitorComponentTypes.Extensions => await ResolveAsync<Extension>(
                input,
                canonicalType,
                (d, k, v, ct) => componentCacheStore.GetExtensionAsync(d, k, v, ct),
                ct => domainCacheContext.Extensions.GetAllByDomainAsync(input.Domain, ct),
                cancellationToken),

            MonitorComponentTypes.Functions => await ResolveAsync<Function>(
                input,
                canonicalType,
                (d, k, v, ct) => componentCacheStore.GetFunctionAsync(d, k, v, ct),
                ct => domainCacheContext.Functions.GetAllByDomainAsync(input.Domain, ct),
                cancellationToken),

            MonitorComponentTypes.Views => await ResolveAsync<View>(
                input,
                canonicalType,
                (d, k, v, ct) => componentCacheStore.GetViewAsync(d, k, v, ct),
                ct => domainCacheContext.Views.GetAllByDomainAsync(input.Domain, ct),
                cancellationToken),

            _ => Result<MonitorComponentResponse>.Fail(
                Error.Validation("component.unknownType",
                    $"Unknown component type '{input.ComponentType}'. " +
                    $"Supported: {string.Join(", ", MonitorComponentTypes.Flows, MonitorComponentTypes.Tasks, MonitorComponentTypes.Schemas, MonitorComponentTypes.Extensions, MonitorComponentTypes.Functions, MonitorComponentTypes.Views)}."))
        };
    }

    private static string? NormalizeComponentType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var t = raw.Trim();
        if (t.Equals(MonitorComponentTypes.Flows, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Flows;
        if (t.Equals(MonitorComponentTypes.Tasks, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Tasks;
        if (t.Equals(MonitorComponentTypes.Schemas, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Schemas;
        if (t.Equals(MonitorComponentTypes.Extensions, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Extensions;
        if (t.Equals(MonitorComponentTypes.Functions, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Functions;
        if (t.Equals(MonitorComponentTypes.Views, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Views;

        return null;
    }

    private async Task<Result<MonitorComponentResponse>> ResolveAsync<T>(
        MonitorGetComponentsInput input,
        string canonicalComponentType,
        Func<string, string, string?, CancellationToken, Task<Result<T>>> getByKey,
        Func<CancellationToken, Task<Result<List<T>>>> getSnapshotAllByDomain,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            var one = await getByKey(input.Domain, input.Key, input.Version, cancellationToken);
            if (!one.IsSuccess)
                return Result<MonitorComponentResponse>.Fail(one.Error);

            return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
            {
                ComponentType = canonicalComponentType,
                Items = [Serialize(one.Value!)]
            });
        }

        return await GetFullListWithSnapshotThenBackendAsync<T>(
            canonicalComponentType,
            input.Domain,
            getSnapshotAllByDomain,
            cancellationToken);
    }

    private async Task<Result<MonitorComponentResponse>> GetFullListWithSnapshotThenBackendAsync<T>(
        string componentType,
        string domain,
        Func<CancellationToken, Task<Result<List<T>>>> getSnapshotAllByDomain,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var snapResult = await getSnapshotAllByDomain(cancellationToken);
        if (snapResult.IsSuccess && snapResult.Value is { Count: > 0 })
        {
            return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
            {
                ComponentType = componentType,
                Items = snapResult.Value!.Select(Serialize).ToList()
            });
        }

        var loadResult = await LoadLatestPerKeyFromRuntimeAndWarmCacheAsync<T>(domain, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<MonitorComponentResponse>.Fail(loadResult.Error);

        return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
        {
            ComponentType = componentType,
            Items = loadResult.Value!.Select(Serialize).ToList()
        });
    }

    private async Task<Result<List<T>>> LoadLatestPerKeyFromRuntimeAndWarmCacheAsync<T>(
        string requestDomain,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        runtimeInfoProvider.Check(requestDomain);

        // Isolated scope: matches RuntimeCacheInitializer / RuntimeCacheBackend so ICurrentSchema and DbContext
        // are not affected by whichever workflow schema the HTTP pipeline last selected.
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();

        var fromDb = (await runtimeService.GetAsync<T>(cancellationToken)).ToList();
        var filtered = fromDb
            .Where(e => e is not null
                        && !string.IsNullOrWhiteSpace(e.Key)
                        && string.Equals(e.Domain, requestDomain, StringComparison.OrdinalIgnoreCase))
            .Cast<T>()
            .ToList();

        var latest = filtered
            .GroupBy(e => e.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(e => e.Version, new SemVersionComparer()).First())
            .ToList();

        foreach (var entity in latest)
        {
            var setResult = await componentCacheStore.SetAsync(entity, cancellationToken);
            if (!setResult.IsSuccess)
                return Result<List<T>>.Fail(setResult.Error);
        }

        return Result<List<T>>.Ok(latest);
    }

    private static JsonElement Serialize<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        return JsonDocument.Parse(json).RootElement;
    }
}
