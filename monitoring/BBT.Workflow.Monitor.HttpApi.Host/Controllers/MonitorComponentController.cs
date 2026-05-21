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
    /// Lists or fetches component definitions using vNext cache layers (snapshot, then store/Redis/DB for key lookups; full list warms from DB when snapshot is cold).
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
    [HttpGet("{domain}/components")]
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
}
