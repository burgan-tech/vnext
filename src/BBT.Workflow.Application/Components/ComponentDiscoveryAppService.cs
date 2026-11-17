using BBT.Aether.Application.Services;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Components.Dtos;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Components;

/// <summary>
/// Read-only implementation of <see cref="IComponentDiscoveryAppService"/>.
/// List and version operations use <see cref="IRuntimeService"/> (schema inferred from the entity
/// type, read as the requested domain) and read version metadata only, never a stored body;
/// single-get operations use <see cref="IComponentCacheStore"/> (Redis-first with DB fallback).
/// No new data-access layer is introduced.
/// </summary>
public sealed class ComponentDiscoveryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IRuntimeService runtimeService,
    IComponentCacheStore componentCacheStore,
    IDomainCacheContext domainCacheContext,
    IVersionPolicySource versionPolicy)
    : ApplicationService(serviceProvider), IComponentDiscoveryAppService
{
    private static readonly ComponentType[] AllTypes = Enum.GetValues<ComponentType>();

    private const int DefaultVersionPageSize = 20;

    /// <summary>Largest version page. A version row is a few strings; the cap only bounds one response.</summary>
    private const int MaxVersionPageSize = 100;

    /// <inheritdoc />
    public async Task<Result<ComponentListResultDto>> ListAllAsync(
        string domain,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        var all = new List<ComponentSummaryDto>();
        foreach (var type in AllTypes)
        {
            all.AddRange(await GetSummariesAsync(domain, type, cancellationToken));
        }

        return Result<ComponentListResultDto>.Ok(Paginate(all, page, pageSize));
    }

    /// <inheritdoc />
    public async Task<Result<ComponentListResultDto>> ListAsync(
        string domain,
        ComponentType type,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        var summaries = await GetSummariesAsync(domain, type, cancellationToken);
        return Result<ComponentListResultDto>.Ok(Paginate(summaries, page, pageSize));
    }

    /// <inheritdoc />
    public async Task<Result<ComponentDetailDto>> GetAsync(
        string domain,
        ComponentType type,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        var token = type.ToToken();
        return type switch
        {
            ComponentType.Workflows => (await componentCacheStore.GetFlowAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Tasks => (await componentCacheStore.GetTaskAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Functions => (await componentCacheStore.GetFunctionAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Views => (await componentCacheStore.GetViewAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Extensions => (await componentCacheStore.GetExtensionAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Schemas => (await componentCacheStore.GetSchemaAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Mappings => (await componentCacheStore.GetMappingAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            ComponentType.Agents => (await componentCacheStore.GetAgentAsync(domain, key, version, cancellationToken))
                .Map(c => ToDetail(token, domain, key, c.Version, c)),
            _ => Result<ComponentDetailDto>.Fail(Error.Validation("validation", $"Unsupported component type '{type}'."))
        };
    }

    /// <inheritdoc />
    public async Task<Result<ComponentVersionListDto>> GetVersionsAsync(
        string domain,
        ComponentType type,
        string key,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        return type switch
        {
            ComponentType.Workflows => await GetVersionsAsync<Definitions.Workflow>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Tasks => await GetVersionsAsync<WorkflowTask>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Functions => await GetVersionsAsync<Function>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Views => await GetVersionsAsync<View>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Extensions => await GetVersionsAsync<Extension>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Schemas => await GetVersionsAsync<SchemaDefinition>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Mappings => await GetVersionsAsync<Mapping>(domain, type, key, page, pageSize, cancellationToken),
            ComponentType.Agents => await GetVersionsAsync<Agent>(domain, type, key, page, pageSize, cancellationToken),
            _ => Result<ComponentVersionListDto>.Fail(Error.Validation("validation", $"Unsupported component type '{type}'."))
        };
    }

    /// <inheritdoc />
    public async Task<Result<MappingCodeDto>> GetMappingCodeAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(domain);

        return (await componentCacheStore.GetMappingAsync(domain, key, version, cancellationToken))
            .Map(m => new MappingCodeDto
            {
                Key = m.Key,
                Domain = m.Domain,
                Version = m.Version,
                Encoding = m.Encoding.Description,
                Code = m.DecodedCode
            });
    }

    private async Task<IReadOnlyList<ComponentSummaryDto>> GetSummariesAsync(
        string domain,
        ComponentType type,
        CancellationToken cancellationToken) => type switch
    {
        ComponentType.Workflows => await ListSummariesAsync<Definitions.Workflow>(domain, type, cancellationToken),
        ComponentType.Tasks => await ListSummariesAsync<WorkflowTask>(domain, type, cancellationToken),
        ComponentType.Functions => await ListSummariesAsync<Function>(domain, type, cancellationToken),
        ComponentType.Views => await ListSummariesAsync<View>(domain, type, cancellationToken),
        ComponentType.Extensions => await ListSummariesAsync<Extension>(domain, type, cancellationToken),
        ComponentType.Schemas => await ListSummariesAsync<SchemaDefinition>(domain, type, cancellationToken),
        ComponentType.Mappings => await ListSummariesAsync<Mapping>(domain, type, cancellationToken),
        ComponentType.Agents => await ListSummariesAsync<Agent>(domain, type, cancellationToken),
        _ => []
    };

    private async Task<IReadOnlyList<ComponentSummaryDto>> ListSummariesAsync<T>(
        string domain,
        ComponentType type,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        // Version metadata only: deserializing every stored body to report a key and a version cost memory
        // in proportion to the whole publish history of every component of the type.
        IReadOnlyList<PublishedComponentVersion> versions;
        using (runtimeInfoProvider.UseDomain(domain))
        {
            versions = await runtimeService.GetPublishedVersionsAsync<T>(cancellationToken);
        }

        var token = type.ToToken();
        var componentTypeKey = type.ToComponentTypeKey();

        return versions
            .Select(v => new ComponentSummaryDto
            {
                Type = token,
                ComponentTypeKey = componentTypeKey,
                Key = v.Key,
                Domain = domain,
                Version = v.Version,
                CreatedAt = v.PublishedAt
            })
            .ToList();
    }

    private async Task<Result<ComponentVersionListDto>> GetVersionsAsync<T>(
        string domain,
        ComponentType type,
        string key,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? DefaultVersionPageSize : Math.Min(pageSize, MaxVersionPageSize);
        var skip = (int)Math.Min((long)(safePage - 1) * safeSize, int.MaxValue);

        IReadOnlyList<string> versions;
        IReadOnlyList<ComponentVersionSummary> rows;
        using (runtimeInfoProvider.UseDomain(domain))
        {
            // The candidate list version resolution itself reads on a cache miss, so Latest below is the
            // answer the cache has to give once its entries expire.
            versions = await runtimeService.GetVersionsAsync<T>(key, cancellationToken);
            if (versions.Count == 0)
                return Result<ComponentVersionListDto>.Fail(CacheErrors.ItemNotFoundInBackend<T>(domain, key, null));

            // One row past the page tells whether a next page exists, without a COUNT.
            rows = await runtimeService.GetVersionPageAsync<T>(key, skip, safeSize + 1, cancellationToken);
        }

        string? pointer = null;
        // The cache set's own rule, so this answer and the cache's agree on whether a pointer applies.
        if (domainCacheContext.Set<T>().AppliesVersionPolicy(key))
        {
            // A failed read fails the answer, as it fails resolution: reporting "no pointer" instead would
            // describe a resolution the runtime does not make.
            var pointerResult = await versionPolicy.GetDefaultVersionAsync(domain, key, cancellationToken);
            if (!pointerResult.IsSuccess)
                return Result<ComponentVersionListDto>.Fail(pointerResult.Error);

            pointer = pointerResult.Value;
        }

        var latest = DefaultVersionResolver.Resolve(versions, pointer, InstanceDataVersionComparer.LatestKeyword);

        return Result<ComponentVersionListDto>.Ok(new ComponentVersionListDto
        {
            Type = type.ToToken(),
            ComponentTypeKey = type.ToComponentTypeKey(),
            Domain = domain,
            Key = key,
            TotalVersions = versions.Count,
            DefaultVersion = pointer,
            Latest = new ComponentLatestResolutionDto
            {
                Version = latest.Version,
                Outcome = ToValue(latest.Outcome),
                Source = ToValue(latest.Source)
            },
            Items = rows
                .Take(safeSize)
                .Select(r => new ComponentVersionItemDto
                {
                    Version = r.Version,
                    PublishedAt = r.PublishedAt,
                    LatestRow = r.IsLatest
                })
                .ToList(),
            Page = safePage,
            PageSize = safeSize,
            HasNext = rows.Count > safeSize
        });
    }

    private static string ToValue(VersionResolutionOutcome outcome) => outcome switch
    {
        VersionResolutionOutcome.Resolved => "resolved",
        VersionResolutionOutcome.NotFound => "notFound",
        VersionResolutionOutcome.NotActivated => "notActivated",
        VersionResolutionOutcome.PointerInvalid => "pointerInvalid",
        _ => outcome.ToString()
    };

    private static string ToValue(VersionResolutionSource source) => source switch
    {
        VersionResolutionSource.Automatic => "automatic",
        VersionResolutionSource.Explicit => "explicit",
        VersionResolutionSource.Default => "default",
        VersionResolutionSource.Ceiling => "ceiling",
        _ => source.ToString()
    };

    private static ComponentDetailDto ToDetail(string token, string domain, string key, string version, object definition) => new()
    {
        Type = token,
        Key = key,
        Domain = domain,
        Version = version,
        Definition = definition
    };

    private static ComponentListResultDto Paginate(IReadOnlyList<ComponentSummaryDto> items, int page, int pageSize)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;

        return new ComponentListResultDto
        {
            Items = items.Skip((safePage - 1) * safeSize).Take(safeSize).ToList(),
            Page = safePage,
            PageSize = safeSize,
            TotalCount = items.Count
        };
    }
}
