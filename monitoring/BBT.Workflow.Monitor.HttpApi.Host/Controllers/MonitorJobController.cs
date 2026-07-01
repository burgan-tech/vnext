using System.ComponentModel.DataAnnotations;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.Results;
using BBT.Workflow.Monitor.Common.DTOs;
using BBT.Workflow.Monitor.Jobs;
using BBT.Workflow.Monitor.Jobs.DTOs;
using BBT.Workflow.Monitor.Jobs.Filters;
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
    /// <summary>Active jobs for a specific workflow, paginated. Accepts an optional createdAt range.</summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="workflow">The workflow (flow) key.</param>
    /// <param name="createdAtGte">Optional inclusive lower bound on job creation time (ISO 8601 UTC).</param>
    /// <param name="createdAtLte">Optional inclusive upper bound on job creation time (ISO 8601 UTC).</param>
    /// <param name="page">Page number (1-based).</param>
    /// <param name="pageSize">Number of jobs per page.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <response code="200">A page of active jobs.</response>
    /// <response code="400">The createdAt range is invalid (only one bound, or gte greater than lte).</response>
    [HttpGet("{domain}/workflows/{workflow}/jobs")]
    [ProducesResponseType(typeof(MonitorPagedResponse<MonitorJobItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetWorkflowJobsAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery(Name = "createdAt[gte]")] DateTime? createdAtGte = null,
        [FromQuery(Name = "createdAt[lte]")] DateTime? createdAtLte = null,
        [FromQuery][Range(1, 1000)] int page = 1,
        [FromQuery][Range(1, 100)] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetActiveJobsInput
        {
            Domain = domain,
            Workflow = workflow,
            Filter = new MonitorJobFilterInput { CreatedAtGte = createdAtGte, CreatedAtLte = createdAtLte },
            Page = page,
            PageSize = pageSize
        };
        return FromResult(await jobService.GetActiveJobsAsync(input, cancellationToken));
    }

    /// <summary>Active jobs across the domain (unpaginated union). A bounded createdAt range is mandatory.</summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="createdAtGte">Inclusive lower bound on job creation time (ISO 8601 UTC). Required.</param>
    /// <param name="createdAtLte">Inclusive upper bound on job creation time (ISO 8601 UTC). Required.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <response code="200">Active jobs union (no pagination metadata).</response>
    /// <response code="400">The createdAt range is missing/invalid, or pagination params were supplied.</response>
    [HttpGet("{domain}/jobs")]
    [ProducesResponseType(typeof(MonitorPagedResponse<MonitorJobItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetDomainJobsAsync(
        [FromRoute] string domain,
        [FromQuery(Name = "createdAt[gte]")] DateTime? createdAtGte = null,
        [FromQuery(Name = "createdAt[lte]")] DateTime? createdAtLte = null,
        CancellationToken cancellationToken = default)
    {
        if (Request.Query.ContainsKey("page") || Request.Query.ContainsKey("pageSize"))
            return FromResult(Result<MonitorPagedResponse<MonitorJobItem>>.Fail(
                Error.Validation(
                    "jobs.paginationNotSupported",
                    "Pagination (page/pageSize) is not supported for the domain-wide jobs query.")));

        var input = new MonitorGetActiveJobsInput
        {
            Domain = domain,
            Workflow = null,
            Filter = new MonitorJobFilterInput { CreatedAtGte = createdAtGte, CreatedAtLte = createdAtLte }
        };
        return FromResult(await jobService.GetActiveJobsAsync(input, cancellationToken));
    }
}
