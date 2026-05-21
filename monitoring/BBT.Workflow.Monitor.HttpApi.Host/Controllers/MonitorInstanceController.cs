using System.ComponentModel.DataAnnotations;
using BBT.Aether;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Pagination;
using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitor.Instances;
using BBT.Workflow.Monitor.Instances.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>
/// Read-only monitoring endpoints for workflow instances.
/// All endpoints are extension-free and optimised for the vnext-forge monitoring dashboard.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/monitor")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorInstanceController(
    IMonitorInstanceQueryService queryService,
    IPaginationLinkGenerator linkGenerator,
    IUrlTemplateBuilder urlTemplateBuilder
) : AetherControllerBase
{
    /// <summary>
    /// Returns a paged list of instances with optional GraphQL filter and sorting.
    /// </summary>
    /// <response code="200">Instance list returned successfully</response>
    [HttpGet("{domain}/workflows/{workflow}/instances")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetInstancesAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string? filter = null,
        [FromQuery] [Range(1, 1000)] int page = 1,
        [FromQuery] [Range(1, 100)] int pageSize = 10,
        [FromQuery] string? sort = null,
        [FromQuery] string? groupBy = null,
        [FromQuery] string? aggregations = null,
        CancellationToken cancellationToken = default
    )
    {
        var route = urlTemplateBuilder.BuildInstanceListUrl(domain, workflow);

        var input = new MonitorGetInstancesInput
        {
            Domain = domain,
            Workflow = workflow,
            Filter = filter,
            Page = page,
            PageSize = pageSize,
            Sort = sort,
            PageUrl = route,
            GroupBy = groupBy,
            Aggregations = aggregations,
        };

        var result = await queryService.GetInstancesAsync(input, cancellationToken);

        if (!result.IsSuccess)
            return FromResult(result);

        var response = result.Value!;
        var tempList = new HateoasPagedList<MonitorInstanceResponse>(
            response.Items.OfType<MonitorInstanceResponse>().ToList(),
            page,
            pageSize,
            response.Items.Count == pageSize
        );

        var hateoasResult = linkGenerator.CreateHateoasResult(
            tempList,
            (IReadOnlyList<MonitorInstanceResponse>)tempList.Items,
            route
        );
        response.Links = hateoasResult.Links;

        return result.ToActionResult(HttpContext);
    }

    /// <summary>
    /// Returns instance metadata and active correlations by key or ID.
    /// </summary>
    /// <response code="200">Instance returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}")]
    [ProducesResponseType(typeof(MonitorInstanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
        };

        var result = await queryService.GetInstanceAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns the latest instance data attributes and full version history.
    /// </summary>
    /// <response code="200">Instance data returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/data")]
    [ProducesResponseType(typeof(MonitorInstanceDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceDataAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceDataInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
        };

        var result = await queryService.GetInstanceDataAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns the ordered state-transition history for an instance.
    /// Use this data to render the flow graph or timeline in vnext-forge.
    /// </summary>
    /// <response code="200">Transition history returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/history")]
    [ProducesResponseType(typeof(MonitorInstanceHistoryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceHistoryAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceHistoryInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
        };

        var result = await queryService.GetInstanceHistoryAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Returns task execution records for a specific transition.
    /// Call this on-demand when the user expands a transition in the flow graph.
    /// </summary>
    /// <param name="transitionId">Required. The ID of the transition whose tasks should be returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Task list returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/tasks")]
    [ProducesResponseType(typeof(List<MonitorInstanceTaskResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceTasksAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] [Required] Guid transitionId,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceTaskInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            TransitionId = transitionId,
        };

        var result = await queryService.GetInstanceTaskAsync(input, cancellationToken);
        return FromResult(result);
    }
}
