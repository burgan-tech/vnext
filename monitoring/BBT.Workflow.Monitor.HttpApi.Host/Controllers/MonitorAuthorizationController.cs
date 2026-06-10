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
    /// Full workflow authorization matrix (workflow-scoped).
    /// Add <c>?role=</c> or <c>?queryRoles[]=</c> to also get an inline authorization verdict.
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
        [FromQuery] List<string>? queryRoles = null,
        [FromQuery] string? transitionKey = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetWorkflowPermissionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Version = version,
            Role = role,
            QueryRoles = queryRoles ?? [],
            TransitionKey = transitionKey
        };
        return FromResult(await authorizationService.GetWorkflowMatrixAsync(input, cancellationToken));
    }

    /// <summary>
    /// Workflow authorization matrix resolved via an instance (instance-scoped convenience).
    /// Add <c>?role=</c> or <c>?queryRoles[]=</c> to also get an inline authorization verdict based on the instance's current state.
    /// </summary>
    /// <response code="200">Matrix returned.</response>
    /// <response code="404">Instance or workflow not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/permissions")]
    [ProducesResponseType(typeof(MonitorAuthorizationMatrixResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstancePermissionsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string? role = null,
        [FromQuery] List<string>? queryRoles = null,
        [FromQuery] string? transitionKey = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstancePermissionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Role = role,
            QueryRoles = queryRoles ?? [],
            TransitionKey = transitionKey
        };
        return FromResult(await authorizationService.GetInstanceMatrixAsync(input, cancellationToken));
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
