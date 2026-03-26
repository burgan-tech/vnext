using System.ComponentModel.DataAnnotations;
using BBT.Aether;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Pagination;
using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Instances;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class InstanceController(
    IInstanceCommandAppService commandAppService,
    IInstanceQueryAppService queryAppService,
    IInstanceRetryAppService retryAppService,
    IHttpContextAccessor httpContextAccessor,
    ISubflowCompletionService subflowCompletionService,
    ISubflowStateService subflowStateService,
    IPaginationLinkGenerator linkGenerator,
    IUrlTemplateBuilder urlTemplateBuilder) : AetherControllerBase
{
    /// <summary>
    /// Starts a new workflow instance.
    /// </summary>
    /// <response code="200">Instance started successfully</response>
    /// <response code="400">Validation failed</response>
    /// <response code="404">Workflow or state not found</response>
    /// <response code="409">Instance with same key already exists</response>
    [HttpPost("{domain}/workflows/{workflow}/instances/start")]
    [ProducesResponseType(typeof(StartInstanceOutput), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> StartAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromBody] CreateInstanceDto request,
        [FromQuery] string? version = null,
        [FromQuery] bool sync = false,
        [FromQuery] string[]? extensions = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = new StartInstanceInput(domain, workflow, version, sync)
        {
            Instance = new CreateInstanceInput
            {
                Key = request?.Key,
                Tags = request?.Tags,
                Attributes = request?.Attributes
            },
            Extensions = extensions
        };
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            input.Headers = httpContext.Request.Headers.ToDictionary(s => s.Key.ToLower(), s => s.Value.FirstOrDefault()?.ToString());
            input.RouteValues = httpContext.Request.RouteValues.ToDictionary(s => s.Key, s => s.Value?.ToString());
        }

        var result = await commandAppService.StartAsync(input, cancellationToken);
        return FromResult(result);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/sub/instances/start")]
    public async Task<IActionResult> StartSubAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromBody] CreateSubInstanceDto request,
        [FromQuery] string? version = null,
        [FromQuery] bool sync = false,
        [FromQuery] string[]? extensions = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = new StartInstanceInput(domain, workflow, version, sync)
        {
            Instance = new CreateInstanceInput
            {
                Id = request.Id,
                Key = request.Key,
                Tags = request.Tags,
                Attributes = request.Attributes,
                Callback = request.Callback,
                ExtraProperties = new ExtraPropertyDictionary(request.ExtraProperties)
            },
            StrictIdempotency = true,
            Extensions = extensions
        };
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            input.Headers = httpContext.Request.Headers.ToDictionary(s => s.Key.ToLower(), s => s.Value.FirstOrDefault()?.ToString());
            input.RouteValues = httpContext.Request.RouteValues.ToDictionary(s => s.Key, s => s.Value?.ToString());
        }

        var result = await commandAppService.StartAsync(input, cancellationToken);
        return FromResult(result);
    }

    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/complete")]
    public async Task<IActionResult> CompleteSubAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromBody] FlowCompletedInput request,
        CancellationToken cancellationToken = default
    )
    {
        await subflowCompletionService.CompletionAsync(request, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Updates parent instance with SubFlow's state change.
    /// Internal endpoint for cross-domain SubFlow state synchronization.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/sub/state")]
    public async Task<IActionResult> UpdateSubFlowStateAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromBody] SubFlowStateChangedInput request,
        CancellationToken cancellationToken = default
    )
    {
        await subflowStateService.UpdateParentStateAsync(request, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Executes a transition on a workflow instance.
    /// </summary>
    /// <response code="200">Transition executed successfully</response>
    /// <response code="400">Validation or state transition rule failed</response>
    /// <response code="403">Transition not authorized for current context</response>
    /// <response code="404">Instance, workflow, or transition not found</response>
    /// <response code="409">Transition already in progress (locked) or SubFlow blocking</response>
    /// <response code="503">Service temporarily unavailable</response>
    [HttpPatch("{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey}")]
    [ProducesResponseType(typeof(TransitionOutput), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> TransitionAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromRoute] string transitionKey,
        [FromBody] TransitionDataInput? data = null,
        [FromQuery] bool sync = false,
        [FromQuery] string[]? extensions = null,
        CancellationToken cancellationToken = default
    )
    {
        var input = new TransitionInput(domain, workflow, data, sync)
        {
            Extensions = extensions
        };
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            input.Headers = httpContext.Request.Headers.ToDictionary(s => s.Key.ToLower(), s => s.Value.FirstOrDefault()?.ToString());
            input.RouteValues = httpContext.Request.RouteValues.ToDictionary(s => s.Key, s => s.Value?.ToString());
        }

        var result = await commandAppService.TransitionAsync(
            instance,
            transitionKey,
            input,
            cancellationToken);
        
        return FromResult(result);
    }

    /// <summary>
    /// Retries a faulted workflow instance by re-executing the incomplete transition.
    /// </summary>
    /// <param name="domain">The domain name.</param>
    /// <param name="workflow">The workflow name.</param>
    /// <param name="instance">The instance identifier (ID or key).</param>
    /// <param name="data">Optional transition data to pass during retry.</param>
    /// <param name="sync">Whether to execute synchronously.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Instance retry executed successfully</response>
    /// <response code="400">Instance is not in faulted state or validation failed</response>
    /// <response code="404">Instance or workflow not found</response>
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/retry")]
    [ProducesResponseType(typeof(RetryInstanceOutput), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetryAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromBody] TransitionDataInput? data = null,
        [FromQuery] bool sync = false,
        CancellationToken cancellationToken = default)
    {
        var httpContext = httpContextAccessor.HttpContext;
        var headers = httpContext?.Request.Headers.ToDictionary(
            s => s.Key.ToLower(),
            s => s.Value.FirstOrDefault()?.ToString()) ?? [];
        var routeValues = httpContext?.Request.RouteValues.ToDictionary(
            r => r.Key,
            r => r.Value?.ToString()) ?? [];

        var input = new RetryInstanceInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Data = data,
            Sync = sync,
            Headers = headers,
            RouteValues = routeValues
        };

        var result = await retryAppService.RetryAsync(input, cancellationToken);
        return FromResult(result);
    }
    
    /// <summary>
    /// Retrieves a workflow instance by key or ID.
    /// </summary>
    /// <response code="200">Instance retrieved successfully</response>
    /// <response code="304">Not modified (ETag match)</response>
    /// <response code="404">Instance not found</response>
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}")]
    [ProducesResponseType(typeof(GetInstanceOutput), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInstanceAsync(
        [FromHeader(Name = "If-None-Match")] string? ifNoneMatch,
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string[]? extensions = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var input = new GetInstanceInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Extensions = extensions,
            IfNoneMatch = ifNoneMatch,
            Version = version,
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var result = await queryAppService.GetInstanceAsync(input, cancellationToken);

        if (result.IsNotModified)
            return StatusCode(304);

        if (result.Result.IsSuccess && result.Result.Value is { } value)
        {
            if (!string.IsNullOrEmpty(value.ETag))
                HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;
            if (!string.IsNullOrEmpty(value.EntityEtag))
                HttpContext.Response.Headers[HeadersConstants.XEntityETag] = value.EntityEtag;
        }

        return FromResult(result.Result);
    }

    /// <summary>
    /// Gets a paged list of workflow instances with optional filter, groupBy, aggregations and orderBy.
    /// </summary>
    /// <param name="sort">OrderBy JSON: single {"field":"createdAt","direction":"desc"} or multiple {"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}. Also accepted as orderBy.</param>
    /// <param name="orderBy">Alias for sort; same JSON format. If both provided, orderBy wins.</param>
    [HttpGet("{domain}/workflows/{workflow}/instances")]
    public async Task<IActionResult> GetInstanceListAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string? filter = null,
        [FromQuery] string[]? extensions = null,
        [FromQuery][Range(1, 1000)] int page = 1,
        [FromQuery][Range(1, 100)] int pageSize = 10,
        [FromQuery] string? sort = null,
        [FromQuery] string? orderBy = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var input = new GetInstanceListInput
        {
            Domain = domain,
            Workflow = workflow,
            Extensions = extensions,
            Page = page,
            PageSize = pageSize,
            PageUrl = urlTemplateBuilder.BuildInstanceListUrl(domain, workflow),
            Filter = filter,
            Sort = orderBy ?? sort,
            Version = version,
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var response = await queryAppService.GetInstanceListAsync(input, cancellationToken);
        if (response.IsSuccess)
        {
            var route = urlTemplateBuilder.BuildInstanceListUrl(domain, workflow);

            // Check if items contain GroupSummary objects (groupBy case) or GetInstanceOutput objects (normal case)
            var firstItem = response.Value!.Items.FirstOrDefault();
            var isGroupByResponse = firstItem is GroupSummary;

            if (isGroupByResponse)
            {
                // For groupBy responses, create a simple paged list structure for HATEOAS links
                var groupSummaries = response.Value!.Items.Cast<GroupSummary>().ToList();
                var tempPagedList = new HateoasPagedList<GroupSummary>(
                    groupSummaries,
                    input.Page,
                    input.PageSize,
                    groupSummaries.Count == input.PageSize);

                var hateoasResult = linkGenerator.CreateHateoasResult(tempPagedList, groupSummaries, route);
                response.Value!.Links = hateoasResult.Links;
            }
            else
            {
                // Normal case: items are GetInstanceOutput objects
                var instanceOutputs = response.Value!.Items.Cast<GetInstanceOutput>().ToList();
                var tempPagedList = new HateoasPagedList<GetInstanceOutput>(
                    instanceOutputs,
                    input.Page,
                    input.PageSize,
                    instanceOutputs.Count == input.PageSize);

                var hateoasResult = linkGenerator.CreateHateoasResult(tempPagedList, instanceOutputs, route);
                response.Value!.Links = hateoasResult.Links;
            }
        }

        return response.ToActionResult(HttpContext);
    }

    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/transitions")]
    public async Task<IActionResult> GetInstanceHistoryAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string[]? extensions = null,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var input = new GetInstanceHistoryInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Extensions = extensions,
            Version = version,
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var response = await queryAppService.GetInstanceHistoryAsync(input, cancellationToken);
        return response.ToActionResult(HttpContext);
    }
    
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/data")]
    public async Task<IActionResult> GetInstanceDataAsync(
        [FromHeader(Name = "If-None-Match")] string? ifNoneMatch,
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var input = new GetInstanceDataInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            IfNoneMatch = ifNoneMatch,
            Version = version,
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var result = await queryAppService.GetInstanceDataAsync(input, cancellationToken);

        if (result.IsNotModified)
            return StatusCode(304);

        if (result.Result.IsSuccess && result.Result.Value is { } value)
        {
            if (!string.IsNullOrEmpty(value.ETag))
                HttpContext.Response.Headers[HeadersConstants.ETag] = value.ETag;
            if (!string.IsNullOrEmpty(value.EntityEtag))
                HttpContext.Response.Headers[HeadersConstants.XEntityETag] = value.EntityEtag;
        }

        return FromResult(result.Result);
    }
} 