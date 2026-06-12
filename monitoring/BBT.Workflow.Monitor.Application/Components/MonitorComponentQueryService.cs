using System.Text.Json;
using System.Text.Json.Serialization;
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new UndefinedJsonElementConverter() }
    };

    private sealed class UndefinedJsonElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => JsonElement.ParseValue(ref reader);

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            if (value.ValueKind == JsonValueKind.Undefined)
                writer.WriteNullValue();
            else
                value.WriteTo(writer);
        }
    }

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
            MonitorComponentTypes.Flows => await ResolveAsync<Definitions.Workflow>(
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
        // Snapshot may be partially warm (individual key lookups populate it one-by-one),
        // so it cannot be used as a reliable source for list queries. Always load from DB
        // for list queries to guarantee completeness, then warm the cache.
        var loadResult = await LoadLatestPerKeyFromRuntimeAndWarmCacheAsync<T>(domain, cancellationToken);
        if (loadResult.IsSuccess && loadResult.Value is { Count: > 0 })
        {
            return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
            {
                ComponentType = componentType,
                Items = loadResult.Value!.Select(Serialize).ToList()
            });
        }

        // DB returned nothing — try snapshot as last resort (may have stale but better than empty)
        var snapResult = await getSnapshotAllByDomain(cancellationToken);
        if (snapResult.IsSuccess && snapResult.Value is { Count: > 0 })
        {
            return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
            {
                ComponentType = componentType,
                Items = snapResult.Value!.Select(Serialize).ToList()
            });
        }

        if (!loadResult.IsSuccess)
            return Result<MonitorComponentResponse>.Fail(loadResult.Error);

        return Result<MonitorComponentResponse>.Ok(new MonitorComponentResponse
        {
            ComponentType = componentType,
            Items = []
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

    /// <inheritdoc />
    public async Task<Result<MonitorComponentStatsResponse>> GetComponentStatsAsync(
        MonitorGetComponentStatsInput input,
        CancellationToken cancellationToken = default)
    {
        var flows      = await CountTypeAsync<Definitions.Workflow>(input.Domain, ct => domainCacheContext.Workflows.GetAllByDomainAsync(input.Domain, ct),   cancellationToken);
        if (!flows.IsSuccess)      return Result<MonitorComponentStatsResponse>.Fail(flows.Error);

        var tasks      = await CountTypeAsync<WorkflowTask>(input.Domain,          ct => domainCacheContext.Tasks.GetAllByDomainAsync(input.Domain, ct),      cancellationToken);
        if (!tasks.IsSuccess)      return Result<MonitorComponentStatsResponse>.Fail(tasks.Error);

        var schemas    = await CountTypeAsync<SchemaDefinition>(input.Domain,      ct => domainCacheContext.Schemas.GetAllByDomainAsync(input.Domain, ct),    cancellationToken);
        if (!schemas.IsSuccess)    return Result<MonitorComponentStatsResponse>.Fail(schemas.Error);

        var views      = await CountTypeAsync<View>(input.Domain,                  ct => domainCacheContext.Views.GetAllByDomainAsync(input.Domain, ct),      cancellationToken);
        if (!views.IsSuccess)      return Result<MonitorComponentStatsResponse>.Fail(views.Error);

        var functions  = await CountTypeAsync<Function>(input.Domain,              ct => domainCacheContext.Functions.GetAllByDomainAsync(input.Domain, ct),  cancellationToken);
        if (!functions.IsSuccess)  return Result<MonitorComponentStatsResponse>.Fail(functions.Error);

        var extensions = await CountTypeAsync<Extension>(input.Domain,             ct => domainCacheContext.Extensions.GetAllByDomainAsync(input.Domain, ct), cancellationToken);
        if (!extensions.IsSuccess) return Result<MonitorComponentStatsResponse>.Fail(extensions.Error);

        return Result<MonitorComponentStatsResponse>.Ok(new MonitorComponentStatsResponse
        {
            Flows      = flows.Value,
            Tasks      = tasks.Value,
            Schemas    = schemas.Value,
            Views      = views.Value,
            Functions  = functions.Value,
            Extensions = extensions.Value,
        });
    }

    private async Task<Result<int>> CountTypeAsync<T>(
        string domain,
        Func<CancellationToken, Task<Result<List<T>>>> getSnapshotAllByDomain,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var snapResult = await getSnapshotAllByDomain(cancellationToken);
        if (snapResult.IsSuccess && snapResult.Value is { Count: > 0 })
            return Result<int>.Ok(snapResult.Value.Count);

        var loadResult = await LoadLatestPerKeyFromRuntimeAndWarmCacheAsync<T>(domain, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<int>.Fail(loadResult.Error);

        return Result<int>.Ok(loadResult.Value!.Count);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorComponentSummaryResponse>> GetComponentSummaryAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        var fullResult = await GetComponentsAsync(input, cancellationToken);
        if (!fullResult.IsSuccess)
            return Result<MonitorComponentSummaryResponse>.Fail(fullResult.Error);

        return Result<MonitorComponentSummaryResponse>.Ok(new MonitorComponentSummaryResponse
        {
            ComponentType = fullResult.Value!.ComponentType,
            Items         = fullResult.Value.Items.Select(ProjectToSummary).ToList()
        });
    }

    /// <inheritdoc />
    public async Task<Result<MonitorComponentDetailResponse>> GetComponentDetailAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        var fullResult = await GetComponentsAsync(input, cancellationToken);
        if (!fullResult.IsSuccess)
            return Result<MonitorComponentDetailResponse>.Fail(fullResult.Error);

        var el = fullResult.Value!.Items.FirstOrDefault();
        if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null)
            return Result<MonitorComponentDetailResponse>.Fail(
                Error.NotFound("component.notFound",
                    $"Component '{input.Key}' not found for type '{input.ComponentType}'."));

        var allVersions = await GetAllVersionsAsync(input, cancellationToken);
        var summary = ProjectToSummary(el);

        string? flow = el.TryGetProperty("flow", out var flowEl) ? flowEl.GetString() : null;

        return Result<MonitorComponentDetailResponse>.Ok(new MonitorComponentDetailResponse
        {
            Key      = summary.Key,
            Version  = summary.Version,
            Domain   = summary.Domain,
            Flow     = flow,
            Labels   = summary.Labels,
            Type     = summary.Type,
            Comment  = summary.Comment,
            Versions = allVersions
        });
    }

    private async Task<List<string>> GetAllVersionsAsync(
        MonitorGetComponentsInput input, CancellationToken cancellationToken)
    {
        var canonicalType = NormalizeComponentType(input.ComponentType);

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();

        IEnumerable<IReference?> all = canonicalType switch
        {
            MonitorComponentTypes.Flows       => await runtimeService.GetAsync<Definitions.Workflow>(cancellationToken),
            MonitorComponentTypes.Tasks       => await runtimeService.GetAsync<WorkflowTask>(cancellationToken),
            MonitorComponentTypes.Schemas     => await runtimeService.GetAsync<SchemaDefinition>(cancellationToken),
            MonitorComponentTypes.Extensions  => await runtimeService.GetAsync<Extension>(cancellationToken),
            MonitorComponentTypes.Functions   => await runtimeService.GetAsync<Function>(cancellationToken),
            MonitorComponentTypes.Views       => await runtimeService.GetAsync<View>(cancellationToken),
            _ => []
        };

        return all
            .Where(x => x is not null
                && string.Equals(x.Key, input.Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Domain, input.Domain, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.Version))
            .Select(x => x!.Version!)
            .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MonitorComponentSummaryItem ProjectToSummary(JsonElement el)
    {
        List<MonitorComponentLabel>? labels = null;
        if (el.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            labels = labelsEl.EnumerateArray()
                .Select(l => new MonitorComponentLabel
                {
                    Language = l.TryGetProperty("language", out var lang) ? lang.GetString() : null,
                    Label    = l.TryGetProperty("label",    out var lbl)  ? lbl.GetString()  : null
                })
                .ToList();

            if (labels.Count == 0) labels = null;
        }

        string? comment = null;
        if (el.TryGetProperty("_comment", out var commentEl) && commentEl.ValueKind == JsonValueKind.String)
            comment = commentEl.GetString();

        JsonElement? type = null;
        if (el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind != JsonValueKind.Undefined)
            type = typeEl.Clone();

        return new MonitorComponentSummaryItem
        {
            Key     = el.TryGetProperty("key",     out var keyEl) ? keyEl.GetString() : null,
            Version = el.TryGetProperty("version", out var verEl) ? verEl.GetString() : null,
            Domain  = el.TryGetProperty("domain",  out var domEl) ? domEl.GetString() : null,
            Labels  = labels,
            Type    = type,
            Comment = comment
        };
    }

    /// <inheritdoc />
    public async Task<Result<MonitorDependencyResponse>> GetWorkflowDependenciesAsync(
        string domain, string workflow, string? version, CancellationToken cancellationToken = default)
    {
        var flowResult = await componentCacheStore.GetFlowAsync(domain, workflow, version, cancellationToken);
        if (!flowResult.IsSuccess || flowResult.Value is not { } flow)
            return Result<MonitorDependencyResponse>.Fail(
                Error.NotFound("workflow.notFound", $"Workflow '{workflow}' definition not found."));

        return Result<MonitorDependencyResponse>.Ok(DependencyExtractor.Extract(flow));
    }

    private static JsonElement Serialize<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
