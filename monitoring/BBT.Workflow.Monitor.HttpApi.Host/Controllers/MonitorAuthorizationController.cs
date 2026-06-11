using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.Authorization;
using BBT.Workflow.Monitor.Authorization.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>Read-only authorization endpoints: permissions matrix and sub-views. Pass role params to get an inline authorization verdict.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorAuthorizationController(
    IMonitorAuthorizationQueryService authorizationService) : AetherControllerBase
{
    /// <summary>
    /// Workflow authorization matrix.
    /// Without <c>?role=</c>: full matrix (queryRoles, states, transitions, functions).
    /// With <c>?role=</c>: only entries where the given role appears are returned.
    /// </summary>
    /// <response code="200">Matrix returned.</response>
    /// <response code="404">Workflow not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/permissions")]
    [ProducesResponseType(typeof(MonitorAuthorizationMatrixResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetWorkflowPermissionsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string? version = null,
        [FromQuery] string? role = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetWorkflowPermissionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Version = version,
            Role = role
        };
        return FromResult(await authorizationService.GetWorkflowMatrixAsync(input, cancellationToken));
    }

    /// <summary>
    /// Instance-scoped permissions view. Returns workflow-level roles, the current state's roles,
    /// transitions available from the current state, and workflow functions — derived from the instance's live state.
    /// Add <c>?role=</c> to filter the response to only entries where that role appears.
    /// </summary>
    /// <response code="200">Instance permissions returned.</response>
    /// <response code="404">Instance or workflow not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/permissions")]
    [ProducesResponseType(typeof(MonitorInstancePermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstancePermissionsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string? role = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstancePermissionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Role = role
        };
        return FromResult(await authorizationService.GetInstancePermissionsAsync(input, cancellationToken));
    }

    /// <summary>Transition-level permissions sub-view (P17).</summary>
    /// <response code="200">Transition permissions returned.</response>
    /// <response code="404">Workflow not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/permissions/transitions")]
    [ProducesResponseType(typeof(MonitorTransitionPermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTransitionPermissionsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetWorkflowPermissionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Version = version
        };
        return FromResult(await authorizationService.GetTransitionPermissionsAsync(input, cancellationToken));
    }

    /// <summary>Function-level permissions sub-view (P19).</summary>
    /// <response code="200">Function permissions returned.</response>
    /// <response code="404">Workflow not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/permissions/functions")]
    [ProducesResponseType(typeof(MonitorFunctionPermissionsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFunctionPermissionsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetWorkflowPermissionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Version = version
        };
        return FromResult(await authorizationService.GetFunctionPermissionsAsync(input, cancellationToken));
    }
}
