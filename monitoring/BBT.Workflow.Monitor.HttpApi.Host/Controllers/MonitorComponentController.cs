using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Components;
using BBT.Workflow.Monitor.Components.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>
/// Read-only monitoring endpoints for workflow component definitions.
/// A single parameterised endpoint serves all component types (flows, tasks, schemas, etc.)
/// so the client specifies which type it needs via the <c>type</c> query parameter.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorComponentController(IMonitorComponentQueryService queryService)
    : AetherControllerBase
{
    /// <summary>
    /// Returns a paged lightweight summary of published components for the given type and domain.
    /// When <paramref name="key"/> is omitted, returns a standard paged list with <c>pagination</c> and <c>items</c>.
    /// When <paramref name="key"/> is provided, returns a single flat detail object (<see cref="MonitorComponentDetailResponse"/>)
    /// that includes all published versions — no <c>items</c> wrapper, paging ignored.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="type">
    /// Component type. Supported values:
    /// <c>sys-flows</c>, <c>sys-tasks</c>, <c>sys-schemas</c>,
    /// <c>sys-extensions</c>, <c>sys-functions</c>, <c>sys-views</c>, <c>sys-mappings</c>.
    /// </param>
    /// <param name="key">Optional single component key. When provided, returns detail or 404. Paging is ignored.</param>
    /// <param name="version">Optional version filter. When omitted, the latest version is returned.</param>
    /// <param name="page">1-based page number (list mode only). Default: 1.</param>
    /// <param name="pageSize">Items per page (list mode only). Range: 1–100. Default: 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Paged summary list or single component detail returned successfully</response>
    /// <response code="400">Unknown component type or invalid pagination parameters</response>
    /// <response code="404">Specific <paramref name="key"/> not found</response>
    [HttpGet("{domain}/components")]
    [ProducesResponseType(typeof(MonitorPagedResponse<MonitorComponentSummaryItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitorComponentDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComponentSummaryAsync(
        [FromRoute] string domain,
        [FromQuery] [Required] string type,
        [FromQuery] string? key = null,
        [FromQuery] string? version = null,
        [FromQuery] [Range(1, 1000)] int page = 1,
        [FromQuery] [Range(1, 100)] int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetComponentsInput
        {
            Domain        = domain.Trim(),
            ComponentType = type.Trim(),
            Key           = string.IsNullOrWhiteSpace(key)     ? null : key.Trim(),
            Version       = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            Page          = page,
            PageSize      = pageSize,
        };

        if (input.Key is not null)
        {
            var detail = await queryService.GetComponentDetailAsync(input, cancellationToken);
            return FromResult(detail);
        }

        var result = await queryService.GetComponentSummaryAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Lists or fetches published component definitions (flows, tasks, schemas, views, functions, extensions).
    /// When <paramref name="key"/> is supplied, returns that single component (no pagination metadata).
    /// When <paramref name="key"/> is omitted, returns a paged list with <c>pagination</c> and <c>items</c>.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="type">
    /// Component type. Supported values:
    /// <c>sys-flows</c>, <c>sys-tasks</c>, <c>sys-schemas</c>,
    /// <c>sys-extensions</c>, <c>sys-functions</c>, <c>sys-views</c>.
    /// </param>
    /// <param name="key">Optional single component key; returns that component or 404. Paging is ignored.</param>
    /// <param name="version">Optional version filter. When omitted, the latest version is returned.</param>
    /// <param name="page">1-based page number (list mode only). Default: 1.</param>
    /// <param name="pageSize">Items per page (list mode only). Range: 1–100. Default: 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Component definitions returned successfully</response>
    /// <response code="400">Unknown component type or invalid pagination parameters</response>
    /// <response code="404">Specific <paramref name="key"/> not found</response>
    [HttpGet("{domain}/components/definition")]
    [ProducesResponseType(typeof(MonitorPagedResponse<JsonElement>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComponentsAsync(
        [FromRoute] string domain,
        [FromQuery] [Required] string type,
        [FromQuery] string? key = null,
        [FromQuery] string? version = null,
        [FromQuery] [Range(1, 1000)] int page = 1,
        [FromQuery] [Range(1, 100)] int pageSize = 20,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetComponentsInput
        {
            Domain        = domain.Trim(),
            ComponentType = type.Trim(),
            Key           = string.IsNullOrWhiteSpace(key)     ? null : key.Trim(),
            Version       = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            Page          = page,
            PageSize      = pageSize,
        };

        var result = await queryService.GetComponentsAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns per-type component counts (flows, tasks, schemas, views, functions, extensions) for the domain.
    /// Useful for a quick inventory overview — "how many components of each type exist in this domain?"
    /// Snapshot cache is used first; falls back to runtime DB load if the snapshot is empty.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Component counts returned successfully</response>
    [HttpGet("{domain}/stats/components")]
    [ProducesResponseType(typeof(MonitorComponentStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetComponentStatsAsync(
        [FromRoute] string domain,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetComponentStatsInput { Domain = domain.Trim() };
        var result = await queryService.GetComponentStatsAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns all component dependencies of a workflow definition
    /// (tasks, schemas, views, functions, extensions, sub-flows) with their reference site.
    /// </summary>
    /// <response code="200">Dependency graph returned.</response>
    /// <response code="404">Workflow definition not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/dependencies")]
    [ProducesResponseType(typeof(MonitorDependencyResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkflowDependenciesAsync(
        [FromRoute] string domain, [FromRoute] string workflow,
        [FromQuery] string? version = null, CancellationToken cancellationToken = default)
        => FromResult(await queryService.GetWorkflowDependenciesAsync(domain, workflow, version, cancellationToken));
}
