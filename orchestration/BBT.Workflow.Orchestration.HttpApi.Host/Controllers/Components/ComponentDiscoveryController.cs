using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.Results;
using BBT.Workflow.Components;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Controllers.Components;

/// <summary>
/// Read-only discovery endpoints for the seven vNext runtime component types
/// (workflows, tasks, functions, views, extensions, schemas, mappings). Wraps the
/// in-process component cache / runtime services and is consumed by the
/// <c>vnext-runtime</c> MCP server. No write operations are exposed here.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class ComponentDiscoveryController(
    IComponentDiscoveryAppService componentDiscoveryAppService) : AetherControllerBase
{
    /// <summary>
    /// Lists component summaries across all types for a domain, paged.
    /// </summary>
    [HttpGet("{domain}/components")]
    public async Task<IActionResult> ListAllAsync(
        [FromRoute] string domain,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await componentDiscoveryAppService.ListAllAsync(domain, page, pageSize, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Lists component summaries of a single type for a domain, paged.
    /// </summary>
    [HttpGet("{domain}/components/{type}")]
    public async Task<IActionResult> ListByTypeAsync(
        [FromRoute] string domain,
        [FromRoute] string type,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        if (!ComponentTypeExtensions.TryParse(type, out var componentType))
            return FromResult(InvalidType(type));

        var result = await componentDiscoveryAppService.ListAsync(domain, componentType, page, pageSize, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Gets a single component definition by type and key (latest version).
    /// </summary>
    [HttpGet("{domain}/components/{type}/{key}")]
    public async Task<IActionResult> GetLatestAsync(
        [FromRoute] string domain,
        [FromRoute] string type,
        [FromRoute] string key,
        CancellationToken cancellationToken = default)
    {
        if (!ComponentTypeExtensions.TryParse(type, out var componentType))
            return FromResult(InvalidType(type));

        var result = await componentDiscoveryAppService.GetAsync(domain, componentType, key, null, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Gets a single component definition by type, key and a specific version.
    /// </summary>
    [HttpGet("{domain}/components/{type}/{key}/{cVersion}")]
    public async Task<IActionResult> GetByVersionAsync(
        [FromRoute] string domain,
        [FromRoute] string type,
        [FromRoute] string key,
        [FromRoute] string cVersion,
        CancellationToken cancellationToken = default)
    {
        if (!ComponentTypeExtensions.TryParse(type, out var componentType))
            return FromResult(InvalidType(type));

        var result = await componentDiscoveryAppService.GetAsync(domain, componentType, key, cVersion, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Gets the decoded <c>.csx</c> code for a mapping component.
    /// </summary>
    [HttpGet("{domain}/components/mappings/{key}/code")]
    public async Task<IActionResult> GetMappingCodeAsync(
        [FromRoute] string domain,
        [FromRoute] string key,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var result = await componentDiscoveryAppService.GetMappingCodeAsync(domain, key, version, cancellationToken);
        return FromResult(result);
    }

    private static Result<object> InvalidType(string type) =>
        Result<object>.Fail(Error.Validation(
            "validation",
            $"Unknown component type '{type}'. Expected one of: workflows, tasks, functions, views, extensions, schemas, mappings."));
}
