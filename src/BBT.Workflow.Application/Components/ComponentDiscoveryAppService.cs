using BBT.Aether.Application.Services;
using BBT.Aether.Auditing;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Components.Dtos;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Components;

/// <summary>
/// Read-only implementation of <see cref="IComponentDiscoveryAppService"/>.
/// List operations use <see cref="IRuntimeService"/> (schema inferred from the entity
/// type, filtered by domain); single-get operations use <see cref="IComponentCacheStore"/>
/// (Redis-first with DB fallback). No new data-access layer is introduced.
/// </summary>
public sealed class ComponentDiscoveryAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IRuntimeService runtimeService,
    IComponentCacheStore componentCacheStore)
    : ApplicationService(serviceProvider), IComponentDiscoveryAppService
{
    private static readonly ComponentType[] AllTypes = Enum.GetValues<ComponentType>();

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
            _ => Result<ComponentDetailDto>.Fail(Error.Validation("validation", $"Unsupported component type '{type}'."))
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
        _ => []
    };

    private async Task<IReadOnlyList<ComponentSummaryDto>> ListSummariesAsync<T>(
        string domain,
        ComponentType type,
        CancellationToken cancellationToken)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var entities = await runtimeService.GetAsync<T>(cancellationToken);
        var token = type.ToToken();

        return entities
            .Where(e => e is not null && string.Equals(e!.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Select(e => ToSummary(token, e!))
            .ToList();
    }

    private static ComponentSummaryDto ToSummary(string token, IDomainEntity entity) => new()
    {
        Type = token,
        ComponentTypeKey = entity.ComponentKey,
        Key = entity.Key,
        Domain = entity.Domain,
        Version = entity.Version,
        CreatedAt = entity is IHasCreatedAt created ? created.CreatedAt : null
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
