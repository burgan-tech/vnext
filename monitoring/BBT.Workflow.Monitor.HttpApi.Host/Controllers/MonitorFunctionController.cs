using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.Functions;
using BBT.Workflow.Monitor.Functions.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>
/// Read-only monitoring endpoints for function definitions.
/// Functions are never executed; these endpoints surface definition metadata only.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorFunctionController(
    IMonitorFunctionQueryService functionQueryService) : AetherControllerBase
{
    /// <summary>
    /// Returns function definitions that have <c>Domain</c> scope for the given domain.
    /// Domain-scoped functions are callable from any workflow without explicit registration.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Function list returned successfully.</response>
    [HttpGet("{domain}/functions/scope")]
    [ProducesResponseType(typeof(MonitorFunctionListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDomainFunctionsAsync(
        [FromRoute] string domain,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetDomainFunctionsInput { Domain = domain };
        var result = await functionQueryService.GetDomainFunctionsAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns all function definitions explicitly registered in the workflow that the given instance
    /// is running.
    /// The instance's workflow version is used so the result matches the definition as it was
    /// when the instance was started.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="workflow">The workflow (flow) key.</param>
    /// <param name="instance">The instance key or ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Function list returned successfully.</response>
    /// <response code="404">Instance or workflow definition not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/functions/scope")]
    [ProducesResponseType(typeof(MonitorFunctionListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceFunctionsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceFunctionsInput
        {
            Domain   = domain,
            Workflow = workflow,
            Instance = instance
        };
        var result = await functionQueryService.GetInstanceFunctionsAsync(input, cancellationToken);
        return FromResult(result);
    }
}
