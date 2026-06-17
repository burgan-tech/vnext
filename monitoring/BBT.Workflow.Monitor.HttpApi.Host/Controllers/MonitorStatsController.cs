using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.Stats;
using BBT.Workflow.Monitor.Stats.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>Read-only aggregation endpoints (instance counters, state distribution) for dashboards.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorStatsController(IMonitorStatsService statsService) : AetherControllerBase
{
    /// <summary>
    /// Returns status-based instance counters for a specific workflow.
    /// </summary>
    /// <response code="200">Counters returned successfully</response>
    [HttpGet("{domain}/workflows/{workflow}/stats/instances")]
    [ProducesResponseType(typeof(MonitorInstanceCountersResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetWorkflowInstanceCountersAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceCountersInput
        {
            Domain   = domain,
            Workflow = workflow,
            Version  = string.IsNullOrWhiteSpace(version) ? null : version.Trim()
        };
        var result = await statsService.GetInstanceCountersAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns status-based instance counters aggregated across all workflows in the domain.
    /// Workflow list is resolved from cache (snapshot) then falls back to the runtime backend.
    /// Each workflow schema is scanned in parallel (one grouped query per schema) and the counts are summed.
    /// Accepts an optional GraphQL <paramref name="filter"/>; when it does not constrain <c>createdAt</c>,
    /// a default "last 7 days" window is applied so very large tables are never scanned unbounded by default.
    /// </summary>
    /// <param name="domain">The tenant/domain key.</param>
    /// <param name="filter">Optional GraphQL filter (e.g. a <c>createdAt</c> date-range). Omitted = last 7 days.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Counters returned successfully</response>
    [HttpGet("{domain}/stats/instances")]
    [ProducesResponseType(typeof(MonitorInstanceCountersResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDomainInstanceCountersAsync(
        [FromRoute] string domain,
        [FromQuery] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceCountersInput
        {
            Domain   = domain,
            Workflow = null,
            Filter   = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim()
        };
        var result = await statsService.GetInstanceCountersAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Live instance distribution across a workflow's states.</summary>
    /// <response code="200">State distribution returned successfully</response>
    /// <response code="404">Workflow definition not found in cache</response>
    [HttpGet("{domain}/workflows/{workflow}/stats/states")]
    [ProducesResponseType(typeof(MonitorStateDistributionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStateDistributionAsync(
        [FromRoute] string domain, [FromRoute] string workflow,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetStateDistributionInput
        {
            Domain   = domain,
            Workflow = workflow,
            Version  = string.IsNullOrWhiteSpace(version) ? null : version.Trim()
        };
        var result = await statsService.GetStateDistributionAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Returns fault statistics: total faulted count, by-state and by-task breakdown, time-window trend (P10).</summary>
    /// <response code="200">Fault stats returned.</response>
    [HttpGet("{domain}/workflows/{workflow}/stats/faults")]
    [ProducesResponseType(typeof(MonitorFaultStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFaultStatsAsync(
        [FromRoute] string domain, [FromRoute] string workflow, CancellationToken cancellationToken = default)
        => FromResult(await statsService.GetFaultStatsAsync(
            new MonitorGetWorkflowStatsInput { Domain = domain, Workflow = workflow }, cancellationToken));

    /// <summary>Returns per-task execution stats: count, avg duration, success/failure rates (P11).</summary>
    /// <response code="200">Task stats returned.</response>
    [HttpGet("{domain}/workflows/{workflow}/stats/tasks")]
    [ProducesResponseType(typeof(MonitorTaskStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTaskStatsAsync(
        [FromRoute] string domain, [FromRoute] string workflow, CancellationToken cancellationToken = default)
        => FromResult(await statsService.GetTaskStatsAsync(
            new MonitorGetWorkflowStatsInput { Domain = domain, Workflow = workflow }, cancellationToken));

    /// <summary>Returns instance completion duration stats: avg/min/max ms, completed count (P12).</summary>
    /// <response code="200">Duration stats returned.</response>
    [HttpGet("{domain}/workflows/{workflow}/stats/duration")]
    [ProducesResponseType(typeof(MonitorDurationStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDurationStatsAsync(
        [FromRoute] string domain, [FromRoute] string workflow, CancellationToken cancellationToken = default)
        => FromResult(await statsService.GetDurationStatsAsync(
            new MonitorGetWorkflowStatsInput { Domain = domain, Workflow = workflow }, cancellationToken));

    /// <summary>Returns per-transition execution stats: count, avg duration, completion rate, trigger breakdown (P13).</summary>
    /// <response code="200">Transition stats returned.</response>
    [HttpGet("{domain}/workflows/{workflow}/stats/transitions")]
    [ProducesResponseType(typeof(MonitorTransitionStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransitionStatsAsync(
        [FromRoute] string domain, [FromRoute] string workflow, CancellationToken cancellationToken = default)
        => FromResult(await statsService.GetTransitionStatsAsync(
            new MonitorGetWorkflowStatsInput { Domain = domain, Workflow = workflow }, cancellationToken));
}
