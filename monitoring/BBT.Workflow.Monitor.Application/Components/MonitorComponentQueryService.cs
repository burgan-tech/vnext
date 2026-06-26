using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Aether.Application.Services;
using BBT.Aether.Domain.Entities;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Mapping = BBT.Workflow.Definitions.Mapping;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Components.DTOs;
using BBT.Workflow.Monitor.Components.Filters;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Monitor.Components;

/// <summary>
/// Read-only query service for workflow component definitions.
/// Single-key resolution uses distributed cache with backend hydration via <see cref="IComponentCacheStore"/>;
/// domain-wide list/stat queries load from the runtime backend (<see cref="IRuntimeService"/>) since the
/// distributed cache exposes only per-key lookups, then warm the cache for subsequent reads.
/// Full-list DB loads are performed in an isolated DI scope (same pattern as <see cref="RuntimeCacheInitializer"/> and RuntimeCacheBackend).
/// so the request-scoped <see cref="ICurrentSchema"/> cannot leak the wrong PostgreSQL schema into definition queries.
/// </summary>
public sealed class MonitorComponentQueryService(
    IServiceProvider serviceProvider,
    IComponentCacheStore componentCacheStore,
    IServiceScopeFactory serviceScopeFactory,
    IRuntimeInfoProvider runtimeInfoProvider)
    : ApplicationService(serviceProvider), IMonitorComponentQueryService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new UndefinedJsonElementConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
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
    public async Task<Result<MonitorPagedResponse<JsonElement>>> GetComponentsAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        var canonicalType = NormalizeComponentType(input.ComponentType);
        if (canonicalType is null)
        {
            return Result<MonitorPagedResponse<JsonElement>>.Fail(
                Error.Validation("component.unknownType",
                    $"Unknown component type '{input.ComponentType}'. " +
                    $"Supported: {string.Join(", ", MonitorComponentTypes.Flows, MonitorComponentTypes.Tasks, MonitorComponentTypes.Schemas, MonitorComponentTypes.Extensions, MonitorComponentTypes.Functions, MonitorComponentTypes.Views, MonitorComponentTypes.Mappings)}."));
        }

        return canonicalType switch
        {
            MonitorComponentTypes.Flows => await ResolveAsync<Definitions.Workflow>(
                input,
                (d, k, v, ct) => componentCacheStore.GetFlowAsync(d, k, v, ct),
                cancellationToken),

            MonitorComponentTypes.Tasks => await ResolveAsync<WorkflowTask>(
                input,
                (d, k, v, ct) => componentCacheStore.GetTaskAsync(d, k, v, ct),
                cancellationToken),

            MonitorComponentTypes.Schemas => await ResolveAsync<SchemaDefinition>(
                input,
                (d, k, v, ct) => componentCacheStore.GetSchemaAsync(d, k, v, ct),
                cancellationToken),

            MonitorComponentTypes.Extensions => await ResolveAsync<Extension>(
                input,
                (d, k, v, ct) => componentCacheStore.GetExtensionAsync(d, k, v, ct),
                cancellationToken),

            MonitorComponentTypes.Functions => await ResolveAsync<Function>(
                input,
                (d, k, v, ct) => componentCacheStore.GetFunctionAsync(d, k, v, ct),
                cancellationToken),

            MonitorComponentTypes.Views => await ResolveAsync<View>(
                input,
                (d, k, v, ct) => componentCacheStore.GetViewAsync(d, k, v, ct),
                cancellationToken),

            MonitorComponentTypes.Mappings => await ResolveAsync<Mapping>(
                input,
                (d, k, v, ct) => componentCacheStore.GetMappingAsync(d, k, v, ct),
                cancellationToken),

            _ => Result<MonitorPagedResponse<JsonElement>>.Fail(
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
        if (t.Equals(MonitorComponentTypes.Mappings, StringComparison.OrdinalIgnoreCase))
            return MonitorComponentTypes.Mappings;

        return null;
    }

    private async Task<Result<MonitorPagedResponse<JsonElement>>> ResolveAsync<T>(
        MonitorGetComponentsInput input,
        Func<string, string, string?, CancellationToken, Task<Result<T>>> getByKey,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        if (!string.IsNullOrWhiteSpace(input.Key))
        {
            // Load all versions for this key from the runtime DB and apply SemVer-aware selection.
            // This guarantees correct resolution even for old versions not in cache and ensures
            // ?version=1.0 matches the latest 1.0.x, ?version=1 matches the latest 1.x.x etc.
            var allEntitiesResult = await LoadAllVersionsForKeyFromRuntimeAsync<T>(
                input.Domain, input.Key, cancellationToken);
            if (!allEntitiesResult.IsSuccess)
                return Result<MonitorPagedResponse<JsonElement>>.Fail(allEntitiesResult.Error);

            var entities = allEntitiesResult.Value!;
            var allVersions = entities
                .Select(e => e.Version!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var resolvedVersion = InstanceDataVersionComparer.FindBestMatch(allVersions, input.Version);
            if (resolvedVersion is null)
            {
                var versionHint = input.Version is not null ? $" at version '{input.Version}'" : string.Empty;
                return Result<MonitorPagedResponse<JsonElement>>.Fail(
                    Error.NotFound("component.notFound",
                        $"Component '{input.Key}'{versionHint} not found for type '{input.ComponentType}'."));
            }

            var matched = entities.FirstOrDefault(e =>
                string.Equals(e.Version, resolvedVersion, StringComparison.OrdinalIgnoreCase));
            if (matched is null)
            {
                return Result<MonitorPagedResponse<JsonElement>>.Fail(
                    Error.NotFound("component.notFound",
                        $"Component '{input.Key}' version '{resolvedVersion}' could not be loaded."));
            }

            return Result<MonitorPagedResponse<JsonElement>>.Ok(new MonitorPagedResponse<JsonElement>
            {
                Items = [Serialize(matched)]
            });
        }

        return await GetFullListFromRuntimeAsync<T>(input, cancellationToken);
    }

    /// <summary>
    /// Loads all published versions of a single component key from the runtime backend (DB).
    /// Used for SemVer-aware version resolution when <c>key</c> is provided.
    /// </summary>
    private async Task<Result<List<T>>> LoadAllVersionsForKeyFromRuntimeAsync<T>(
        string domain, string key, CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        runtimeInfoProvider.Check(domain);

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var runtimeService = scope.ServiceProvider.GetRequiredService<IRuntimeService>();

        var all = (await runtimeService.GetAsync<T>(cancellationToken))
            .Where(e => e is not null
                        && string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(e.Domain, domain, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(e.Version))
            .Cast<T>()
            .ToList();

        return Result<List<T>>.Ok(all);
    }

    private async Task<Result<MonitorPagedResponse<JsonElement>>> GetFullListFromRuntimeAsync<T>(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        // The distributed cache exposes only per-key lookups (ICacheSet has no domain-wide
        // enumeration), so list queries always load from the runtime backend (DB) to guarantee
        // completeness, then warm the cache for subsequent single-key reads.
        var loadResult = await LoadLatestPerKeyFromRuntimeAndWarmCacheAsync<T>(input.Domain, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<MonitorPagedResponse<JsonElement>>.Fail(loadResult.Error);

        var allItems = loadResult.Value!.Select(Serialize).ToList();
        var pagedItems = allItems
            .Skip((input.Page - 1) * input.PageSize)
            .Take(input.PageSize)
            .ToList();

        return Result<MonitorPagedResponse<JsonElement>>.Ok(new MonitorPagedResponse<JsonElement>
        {
            Pagination = new MonitorPaginationInfo
            {
                Page     = input.Page,
                PageSize = input.PageSize,
                HasNext  = allItems.Count > input.Page * input.PageSize
            },
            Items = pagedItems
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
        var flows      = await CountTypeAsync<Definitions.Workflow>(input.Domain, cancellationToken);
        if (!flows.IsSuccess)      return Result<MonitorComponentStatsResponse>.Fail(flows.Error);

        var tasks      = await CountTypeAsync<WorkflowTask>(input.Domain,          cancellationToken);
        if (!tasks.IsSuccess)      return Result<MonitorComponentStatsResponse>.Fail(tasks.Error);

        var schemas    = await CountTypeAsync<SchemaDefinition>(input.Domain,      cancellationToken);
        if (!schemas.IsSuccess)    return Result<MonitorComponentStatsResponse>.Fail(schemas.Error);

        var views      = await CountTypeAsync<View>(input.Domain,                  cancellationToken);
        if (!views.IsSuccess)      return Result<MonitorComponentStatsResponse>.Fail(views.Error);

        var functions  = await CountTypeAsync<Function>(input.Domain,              cancellationToken);
        if (!functions.IsSuccess)  return Result<MonitorComponentStatsResponse>.Fail(functions.Error);

        var extensions = await CountTypeAsync<Extension>(input.Domain,             cancellationToken);
        if (!extensions.IsSuccess) return Result<MonitorComponentStatsResponse>.Fail(extensions.Error);

        var mappings   = await CountTypeAsync<Mapping>(input.Domain,               cancellationToken);
        if (!mappings.IsSuccess)   return Result<MonitorComponentStatsResponse>.Fail(mappings.Error);

        return Result<MonitorComponentStatsResponse>.Ok(new MonitorComponentStatsResponse
        {
            Flows      = flows.Value,
            Tasks      = tasks.Value,
            Schemas    = schemas.Value,
            Views      = views.Value,
            Functions  = functions.Value,
            Extensions = extensions.Value,
            Mappings   = mappings.Value,
        });
    }

    private async Task<Result<int>> CountTypeAsync<T>(
        string domain,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var loadResult = await LoadLatestPerKeyFromRuntimeAndWarmCacheAsync<T>(domain, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<int>.Fail(loadResult.Error);

        return Result<int>.Ok(loadResult.Value!.Count);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorPagedResponse<MonitorComponentSummaryItem>>> GetComponentSummaryAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        var canonicalType = NormalizeComponentType(input.ComponentType);
        if (canonicalType is null)
            return Result<MonitorPagedResponse<MonitorComponentSummaryItem>>.Fail(
                Error.Validation("component.unknownType",
                    $"Unknown component type '{input.ComponentType}'."));

        var loadResult = await LoadSummaryWithMetadataAsync(input.Domain, canonicalType, cancellationToken);
        if (!loadResult.IsSuccess)
            return Result<MonitorPagedResponse<MonitorComponentSummaryItem>>.Fail(loadResult.Error);

        var projected = loadResult.Value!
            .Select(e => ProjectToSummary(e.Serialized, e.FlowVersion, e.Tags, e.CreatedAt, e.ModifiedAt, canonicalType));

        var allItems = (input.Filter is not null && !input.Filter.IsEmpty)
            ? MonitorComponentFilter.Apply(projected, input.Filter).ToList()
            : projected.ToList();

        var pagedItems = allItems
            .Skip((input.Page - 1) * input.PageSize)
            .Take(input.PageSize)
            .ToList();

        return Result<MonitorPagedResponse<MonitorComponentSummaryItem>>.Ok(new MonitorPagedResponse<MonitorComponentSummaryItem>
        {
            Pagination = new MonitorPaginationInfo
            {
                Page     = input.Page,
                PageSize = input.PageSize,
                HasNext  = allItems.Count > input.Page * input.PageSize
            },
            Items = pagedItems
        });
    }

    /// <inheritdoc />
    public async Task<Result<JsonElement>> GetSingleComponentAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        var listResult = await GetComponentsAsync(input, cancellationToken);
        if (!listResult.IsSuccess)
            return Result<JsonElement>.Fail(listResult.Error);

        return Result<JsonElement>.Ok(listResult.Value!.Items[0]);
    }

    /// <inheritdoc />
    public async Task<Result<MonitorComponentDetailResponse>> GetComponentDetailAsync(
        MonitorGetComponentsInput input,
        CancellationToken cancellationToken = default)
    {
        var canonicalType = NormalizeComponentType(input.ComponentType);
        if (canonicalType is null)
            return Result<MonitorComponentDetailResponse>.Fail(
                Error.Validation("component.unknownType",
                    $"Unknown component type '{input.ComponentType}'."));

        var fullResult = await GetComponentsAsync(input, cancellationToken);
        if (!fullResult.IsSuccess)
            return Result<MonitorComponentDetailResponse>.Fail(fullResult.Error);

        var el = fullResult.Value!.Items.FirstOrDefault();
        if (el.ValueKind == JsonValueKind.Undefined || el.ValueKind == JsonValueKind.Null)
            return Result<MonitorComponentDetailResponse>.Fail(
                Error.NotFound("component.notFound",
                    $"Component '{input.Key}' not found for type '{input.ComponentType}'."));

        var (flowVersion, tags, createdAt, modifiedAt) = await GetInstanceMetaAsync(input.Domain, canonicalType, input.Key!, cancellationToken);
        var allVersions = await GetAllVersionsAsync(input, cancellationToken);
        var summary = ProjectToSummary(el, flowVersion, tags, createdAt, modifiedAt, canonicalType);

        return Result<MonitorComponentDetailResponse>.Ok(new MonitorComponentDetailResponse
        {
            Key         = summary.Key,
            Version     = summary.Version,
            Domain      = summary.Domain,
            Flow        = summary.Flow,
            FlowVersion = summary.FlowVersion,
            Tags        = summary.Tags,
            Labels      = summary.Labels,
            Type        = summary.Type,
            Scope       = summary.Scope,
            Display     = summary.Display,
            Renderer    = summary.Renderer,
            Name        = summary.Name,
            CreatedAt   = summary.CreatedAt,
            ModifiedAt  = summary.ModifiedAt,
            Versions    = allVersions
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
            MonitorComponentTypes.Mappings    => await runtimeService.GetAsync<Mapping>(cancellationToken),
            _ => []
        };

        return all
            .Where(x => x is not null
                && string.Equals(x.Key, input.Key, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Domain, input.Domain, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.Version))
            .Select(x => x!.Version!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(v => v, InstanceDataVersionComparer.StringVersionComparer.Instance)
            .ToList();
    }

    // ── Metadata-aware loading ──────────────────────────────────────────────

    private sealed record ComponentEntry(JsonElement Serialized, string? FlowVersion, List<string>? Tags, DateTime? CreatedAt, DateTime? ModifiedAt);

    private Task<Result<List<ComponentEntry>>> LoadSummaryWithMetadataAsync(
        string requestDomain, string componentType, CancellationToken ct) =>
        componentType switch
        {
            MonitorComponentTypes.Flows      => LoadLatestWithMetadataAsync<Definitions.Workflow>(requestDomain, componentType, ct),
            MonitorComponentTypes.Tasks      => LoadLatestWithMetadataAsync<WorkflowTask>(requestDomain, componentType, ct),
            MonitorComponentTypes.Schemas    => LoadLatestWithMetadataAsync<SchemaDefinition>(requestDomain, componentType, ct),
            MonitorComponentTypes.Extensions => LoadLatestWithMetadataAsync<Extension>(requestDomain, componentType, ct),
            MonitorComponentTypes.Functions  => LoadLatestWithMetadataAsync<Function>(requestDomain, componentType, ct),
            MonitorComponentTypes.Views      => LoadLatestWithMetadataAsync<View>(requestDomain, componentType, ct),
            MonitorComponentTypes.Mappings   => LoadLatestWithMetadataAsync<Mapping>(requestDomain, componentType, ct),
            _ => Task.FromResult(Result<List<ComponentEntry>>.Fail(
                Error.Validation("component.unknownType", $"Unknown component type '{componentType}'.")))
        };

    private async Task<Result<List<ComponentEntry>>> LoadLatestWithMetadataAsync<T>(
        string requestDomain, string componentType, CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        runtimeInfoProvider.Check(requestDomain);

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var instanceRepo  = scope.ServiceProvider.GetRequiredService<IInstanceRepository>();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

        var allItems = new List<(T Entity, string? FlowVersion, List<string>? Tags, DateTime CreatedAt, DateTime? ModifiedAt)>();

        using (currentSchema.Use(componentType))
        {
            const int pageSize = 100;
            int skip = 0;
            List<ActiveInstanceDataSummary> page;

            do
            {
                page = await instanceRepo.GetActiveDataSummariesPagedAsync(skip, pageSize, cancellationToken);
                foreach (var item in page)
                {
                    try
                    {
                        var entity = item.DataBlob
                            .Deserialize<T>(JsonSerializerConstants.JsonOptions);
                        if (entity is null) continue;

                        entity.SetReference(new Reference(
                            item.Key ?? string.Empty,
                            requestDomain,
                            componentType,
                            item.DataVersion));

                        allItems.Add((entity, item.FlowVersion, item.Tags, item.CreatedAt, item.ModifiedAt));
                    }
                    catch { /* skip malformed records */ }
                }

                skip += pageSize;
            }
            while (page.Count == pageSize);
        }

        var latest = allItems
            .Where(x => !string.IsNullOrWhiteSpace(x.Entity.Key))
            .GroupBy(x => x.Entity.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Entity.Version, new SemVersionComparer()).First())
            .ToList();

        foreach (var (entity, _, _, _, _) in latest)
        {
            var setResult = await componentCacheStore.SetAsync(entity, cancellationToken);
            if (!setResult.IsSuccess)
                return Result<List<ComponentEntry>>.Fail(setResult.Error);
        }

        var entries = latest
            .Select(x => new ComponentEntry(Serialize(x.Entity), x.FlowVersion, x.Tags, x.CreatedAt, x.ModifiedAt))
            .ToList();

        return Result<List<ComponentEntry>>.Ok(entries);
    }

    private async Task<(string? FlowVersion, List<string>? Tags, DateTime? CreatedAt, DateTime? ModifiedAt)> GetInstanceMetaAsync(
        string domain, string componentType, string key, CancellationToken cancellationToken)
    {
        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var instanceRepo  = scope.ServiceProvider.GetRequiredService<IInstanceRepository>();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

        using (currentSchema.Use(componentType))
        {
            var instance = await instanceRepo.FindByIdentifierSlimAsync(key, cancellationToken);
            return (instance?.FlowVersion, instance?.Tags, instance?.CreatedAt, instance?.ModifiedAt);
        }
    }

    // ── Projection helpers ───────────────────────────────────────────────────

    private static MonitorComponentSummaryItem ProjectToSummary(
        JsonElement el, string? flowVersion, List<string>? tags, DateTime? createdAt, DateTime? modifiedAt, string componentType)
    {
        var labels = ExtractLabels(el);
        var type   = el.TryGetProperty("type", out var typeEl) && typeEl.ValueKind != JsonValueKind.Undefined
                     ? typeEl.Clone()
                     : (JsonElement?)null;

        string? scope    = el.TryGetProperty("scope",    out var scopeEl)    && scopeEl.ValueKind    == JsonValueKind.String ? scopeEl.GetString()    : null;
        string? display  = el.TryGetProperty("display",  out var displayEl)  && displayEl.ValueKind  == JsonValueKind.String ? displayEl.GetString()  : null;
        string? renderer = el.TryGetProperty("renderer", out var rendererEl) && rendererEl.ValueKind == JsonValueKind.String ? rendererEl.GetString() : null;
        string? name     = el.TryGetProperty("name",     out var nameEl)     && nameEl.ValueKind     == JsonValueKind.String ? nameEl.GetString()     : null;
        return new MonitorComponentSummaryItem
        {
            Key         = el.TryGetProperty("key",     out var keyEl) ? keyEl.GetString() : null,
            Version     = el.TryGetProperty("version", out var verEl) ? verEl.GetString() : null,
            Domain      = el.TryGetProperty("domain",  out var domEl) ? domEl.GetString() : null,
            Flow        = el.TryGetProperty("flow",    out var flwEl) ? flwEl.GetString() : null,
            FlowVersion = string.IsNullOrWhiteSpace(flowVersion) ? null : flowVersion,
            Tags        = tags is { Count: > 0 } ? tags : null,
            Labels      = componentType == MonitorComponentTypes.Tasks ? null : labels,
            Type        = type,
            Scope       = componentType is MonitorComponentTypes.Functions or MonitorComponentTypes.Extensions ? scope : null,
            Display     = componentType == MonitorComponentTypes.Views ? display : null,
            Renderer    = componentType == MonitorComponentTypes.Views ? renderer : null,
            Name        = componentType == MonitorComponentTypes.Mappings ? name : null,
            CreatedAt   = createdAt,
            ModifiedAt  = modifiedAt
        };
    }

    private static List<MonitorComponentLabel>? ExtractLabels(JsonElement el)
    {
        if (!el.TryGetProperty("labels", out var labelsEl) || labelsEl.ValueKind != JsonValueKind.Array)
            return null;

        var labels = labelsEl.EnumerateArray()
            .Select(l => new MonitorComponentLabel
            {
                Language = l.TryGetProperty("language", out var lang) ? lang.GetString() : null,
                Label    = l.TryGetProperty("label",    out var lbl)  ? lbl.GetString()  : null
            })
            .ToList();

        return labels.Count == 0 ? null : labels;
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

    /// <inheritdoc />
    public async Task<Result<MonitorPagedResponse<MonitorComponentVersionItem>>> GetComponentVersionsAsync(
        MonitorGetComponentVersionsInput input, CancellationToken cancellationToken = default)
    {
        var componentType = NormalizeComponentType(input.ComponentType);
        if (componentType is null)
            return Result<MonitorPagedResponse<MonitorComponentVersionItem>>.Fail(
                Error.Validation("component.invalidType",
                    $"Unknown component type '{input.ComponentType}'. " +
                    $"Supported: {string.Join(", ", MonitorComponentTypes.Flows, MonitorComponentTypes.Tasks, MonitorComponentTypes.Schemas, MonitorComponentTypes.Extensions, MonitorComponentTypes.Functions, MonitorComponentTypes.Views, MonitorComponentTypes.Mappings)}."));

        var skip = (input.Page - 1) * input.PageSize;

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var instanceRepo  = scope.ServiceProvider.GetRequiredService<IInstanceRepository>();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();

        using (currentSchema.Use(componentType))
        {
            var raw = await instanceRepo.GetVersionsPagedAsync(
                input.Key, skip, input.PageSize + 1, cancellationToken);

            if (raw.Count == 0 && input.Page == 1)
                return Result<MonitorPagedResponse<MonitorComponentVersionItem>>.Fail(
                    Error.NotFound("component.notFound",
                        $"Component '{input.Key}' not found in '{componentType}'."));

            var hasNext = raw.Count > input.PageSize;
            var items   = raw.Take(input.PageSize)
                             .Select(v => new MonitorComponentVersionItem
                             {
                                 Version     = v.Version,
                                 IsLatest    = v.IsLatest,
                                 FlowVersion = v.FlowVersion,
                                 PublishedAt = v.PublishedAt
                             })
                             .ToList();

            return Result<MonitorPagedResponse<MonitorComponentVersionItem>>.Ok(
                new MonitorPagedResponse<MonitorComponentVersionItem>
                {
                    Pagination = new MonitorPaginationInfo
                    {
                        Page     = input.Page,
                        PageSize = input.PageSize,
                        HasNext  = hasNext
                    },
                    Items = items
                });
        }
    }

    private static JsonElement Serialize<T>(T value) where T : class
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
