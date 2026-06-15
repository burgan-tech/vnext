using System.ComponentModel.DataAnnotations;
using BBT.Aether;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Application.Pagination;
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

        response.Links = linkGenerator.Relative().GenerateLinks(tempList, route);

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
    /// When <paramref name="version"/> is specified, returns only that single data version.
    /// </summary>
    /// <response code="200">Instance data returned successfully</response>
    /// <response code="404">Instance or data version not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/data")]
    [ProducesResponseType(typeof(MonitorInstanceDataResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceDataAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceDataInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version,
        };

        var result = await queryService.GetInstanceDataAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Returns the view bound to the instance's current state or a given transition.</summary>
    /// <response code="200">View returned (with candidates when rule-based).</response>
    /// <response code="204">No view is defined for the current state or specified transition.</response>
    /// <response code="404">Instance, workflow or transition not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/view")]
    [ProducesResponseType(typeof(MonitorInstanceViewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceViewAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string? transitionKey = null,
        [FromQuery] string? role = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceViewInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            TransitionKey = transitionKey,
            Role = role,
            Version = version
        };
        var result = await queryService.GetInstanceViewAsync(input, cancellationToken);
        if (result.IsSuccess && result.Value is null)
            return NoContent();
        return FromResult(result);
    }

    /// <summary>
    /// Returns the instance timeline. Behaviour depends on the optional query parameters:
    /// no identifier returns the full ordered transition timeline; transitionId returns a single
    /// transition's details; taskId returns a single task execution record. includeTasks embeds
    /// each transition's task records (ignored in single-task mode).
    /// </summary>
    /// <param name="domain">Tenant/domain key.</param>
    /// <param name="workflow">Workflow (flow) key.</param>
    /// <param name="instance">Instance key or ID.</param>
    /// <param name="transitionId">Optional. Returns only this transition's details.</param>
    /// <param name="taskId">Optional. Returns only this single task; takes precedence over transitionId.</param>
    /// <param name="includeTasks">Embeds task records into each returned transition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Timeline returned successfully</response>
    /// <response code="400">transitionId or taskId is supplied but empty</response>
    /// <response code="404">Instance, transition, or task not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/timeline")]
    [ProducesResponseType(typeof(MonitorInstanceTimelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceTimelineAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] Guid? transitionId = null,
        [FromQuery] Guid? taskId = null,
        [FromQuery] bool includeTasks = false,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceTimelineInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            TransitionId = transitionId,
            TaskId = taskId,
            IncludeTasks = includeTasks,
        };

        var result = await queryService.GetInstanceTimelineAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Returns the instance's current state and the transitions available from it.</summary>
    /// <response code="200">State returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/state")]
    [ProducesResponseType(typeof(MonitorInstanceStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceStateAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default
    )
    {
        var input = new MonitorGetInstanceStateInput { Domain = domain, Workflow = workflow, Instance = instance };
        var result = await queryService.GetInstanceStateAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Returns fault detail (failed tasks + unfinished transition) for a faulted instance.</summary>
    /// <response code="200">Fault detail returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/faults")]
    [ProducesResponseType(typeof(MonitorInstanceFaultResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceFaultsAsync(
        [FromRoute] string domain, [FromRoute] string workflow, [FromRoute] string instance,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceFaultsInput { Domain = domain, Workflow = workflow, Instance = instance };
        var result = await queryService.GetInstanceFaultsAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Returns the field-level diff between two instance data versions.</summary>
    /// <response code="200">Diff returned successfully</response>
    /// <response code="404">Instance or data version not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/data/diff")]
    [ProducesResponseType(typeof(MonitorInstanceDataDiffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceDataDiffAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] [Required] string from,
        [FromQuery] [Required] string to,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceDataDiffInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            From = from,
            To = to
        };
        var result = await queryService.GetInstanceDataDiffAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>Returns the recursive sub-flow/sub-process hierarchy tree for an instance.</summary>
    /// <response code="200">Hierarchy returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/hierarchy")]
    [ProducesResponseType(typeof(MonitorHierarchyNode), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceHierarchyAsync(
        [FromRoute] string domain, [FromRoute] string workflow, [FromRoute] string instance,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceHierarchyInput { Domain = domain, Workflow = workflow, Instance = instance };
        var result = await queryService.GetInstanceHierarchyAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Reverse navigates from a sub-flow instance to its parent.
    /// Returns Parent = null for a root (top-level) instance.
    /// </summary>
    /// <response code="200">Parent returned (or null for root).</response>
    /// <response code="404">Instance not found.</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/parent")]
    [ProducesResponseType(typeof(MonitorParentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceParentAsync(
        [FromRoute] string domain, [FromRoute] string workflow, [FromRoute] string instance,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetParentInput { Domain = domain, Workflow = workflow, Instance = instance };
        return FromResult(await queryService.GetInstanceParentAsync(input, cancellationToken));
    }

    /// <summary>
    /// Returns all tasks the instance has executed, ordered by StartedAt ascending,
    /// enriched with the transition key and state context.
    /// </summary>
    /// <response code="200">Task list returned successfully</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/tasks")]
    [ProducesResponseType(typeof(MonitorInstanceTaskListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceTasksAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceTasksInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance
        };
        return FromResult(await queryService.GetInstanceTaskListAsync(input, cancellationToken));
    }

    /// <summary>
    /// Returns the full detail of a single task execution, including definition config,
    /// trigger slot (OnExecute / OnExit / OnEntry), and input/output payloads.
    /// Definition and trigger context are best-effort — null when the definition is unavailable.
    /// </summary>
    /// <response code="200">Task detail returned successfully</response>
    /// <response code="404">Instance or task not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/tasks/{taskId:guid}")]
    [ProducesResponseType(typeof(MonitorTaskDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceTaskDetailAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromRoute] Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var input = new MonitorGetInstanceTaskDetailInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            TaskId = taskId
        };
        return FromResult(await queryService.GetInstanceTaskDetailAsync(input, cancellationToken));
    }

}
