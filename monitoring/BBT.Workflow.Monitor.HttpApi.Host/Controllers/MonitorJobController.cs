using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.Jobs;
using BBT.Workflow.Monitor.Jobs.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>Read-only active job/timer listing (P7).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorJobController(IMonitorJobQueryService jobService) : AetherControllerBase
{
    /// <summary>Active jobs for a specific workflow.</summary>
    /// <response code="200">Active jobs returned.</response>
    [HttpGet("{domain}/workflows/{workflow}/jobs")]
    [ProducesResponseType(typeof(MonitorActiveJobsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkflowJobsAsync(
        [FromRoute] string domain, [FromRoute] string workflow,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetActiveJobsInput { Domain = domain, Workflow = workflow };
        return FromResult(await jobService.GetActiveJobsAsync(input, cancellationToken));
    }

    /// <summary>Active jobs across the domain (best-effort: resolved schema only).</summary>
    /// <response code="200">Active jobs returned.</response>
    [HttpGet("{domain}/jobs")]
    [ProducesResponseType(typeof(MonitorActiveJobsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDomainJobsAsync(
        [FromRoute] string domain,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetActiveJobsInput { Domain = domain, Workflow = null };
        return FromResult(await jobService.GetActiveJobsAsync(input, cancellationToken));
    }
}
