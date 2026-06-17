using BBT.Aether.Application;
using BBT.Aether.Results;
using BBT.Workflow.Components.Dtos;

namespace BBT.Workflow.Components;

/// <summary>
/// Read-only discovery surface over the seven vNext runtime component types
/// (workflows, tasks, functions, views, extensions, schemas, mappings). Wraps the
/// in-process component cache / runtime services so external callers — and the
/// <c>vnext-runtime</c> MCP server — can list and read component definitions.
/// </summary>
public interface IComponentDiscoveryAppService : IApplicationService
{
    /// <summary>
    /// Lists component summaries of every type for a domain, paged.
    /// </summary>
    Task<Result<ComponentListResultDto>> ListAllAsync(
        string domain,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists component summaries of a single type for a domain, paged.
    /// </summary>
    Task<Result<ComponentListResultDto>> ListAsync(
        string domain,
        ComponentType type,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the full definition of a single component by type, key and optional version
    /// (latest when <paramref name="version"/> is null/empty).
    /// </summary>
    Task<Result<ComponentDetailDto>> GetAsync(
        string domain,
        ComponentType type,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the decoded <c>.csx</c> code for a mapping component.
    /// </summary>
    Task<Result<MappingCodeDto>> GetMappingCodeAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);
}
