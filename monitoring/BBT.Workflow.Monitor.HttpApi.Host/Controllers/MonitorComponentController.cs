using System.ComponentModel.DataAnnotations;
using BBT.Aether.AspNetCore.Controllers;
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
    /// Returns a lightweight summary of published components for the given type and domain.
    /// When <paramref name="key"/> is omitted, returns a list of all published components (<see cref="MonitorComponentSummaryResponse"/>).
    /// When <paramref name="key"/> is provided, returns a single flat detail object (<see cref="MonitorComponentDetailResponse"/>)
    /// that includes the component's <c>flow</c> identifier and all published versions — no <c>items</c> wrapper.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="type">
    /// Component type. Supported values:
    /// <c>sys-flows</c>, <c>sys-tasks</c>, <c>sys-schemas</c>,
    /// <c>sys-extensions</c>, <c>sys-functions</c>, <c>sys-views</c>.
    /// </param>
    /// <param name="key">Optional single component key. When provided, returns detail or 404.</param>
    /// <param name="version">Optional version filter. When omitted, the latest version is returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Summary list or single component detail returned successfully</response>
    /// <response code="400">Unknown component type</response>
    /// <response code="404">Specific <paramref name="key"/> not found</response>
    [HttpGet("{domain}/components")]
    [ProducesResponseType(typeof(MonitorComponentSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(MonitorComponentDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComponentSummaryAsync(
        [FromRoute] string domain,
        [FromQuery] [Required] string type,
        [FromQuery] string? key = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetComponentsInput
        {
            Domain        = domain.Trim(),
            ComponentType = type.Trim(),
            Key           = string.IsNullOrWhiteSpace(key)     ? null : key.Trim(),
            Version       = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
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
    /// When <paramref name="key"/> is supplied without <paramref name="version"/>, the latest version is returned;
    /// when <paramref name="version"/> is supplied, that exact version is returned.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="type">
    /// Component type. Supported values:
    /// <c>sys-flows</c>, <c>sys-tasks</c>, <c>sys-schemas</c>,
    /// <c>sys-extensions</c>, <c>sys-functions</c>, <c>sys-views</c>.
    /// </param>
    /// <param name="key">Optional single component key; returns that component or 404.</param>
    /// <param name="version">Optional version filter. When omitted, the latest version is returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Component definitions returned successfully</response>
    /// <response code="400">Unknown component type</response>
    /// <response code="404">Specific <paramref name="key"/> not found</response>
    [HttpGet("{domain}/components/definition")]
    [ProducesResponseType(typeof(MonitorComponentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetComponentsAsync(
        [FromRoute] string domain,
        [FromQuery] [Required] string type,
        [FromQuery] string? key = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetComponentsInput
        {
            Domain = domain.Trim(),
            ComponentType = type.Trim(),
            Key = string.IsNullOrWhiteSpace(key) ? null : key.Trim(),
            Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
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
