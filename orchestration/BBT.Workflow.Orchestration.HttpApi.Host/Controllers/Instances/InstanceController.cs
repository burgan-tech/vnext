using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Aether;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Results;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Events;
using BBT.Workflow.Execution.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
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
    ISubflowFaultService subflowFaultService,
    IInstanceCancellationService cancellationService,
    IChildSubflowCancellationService childSubflowCancellationService,
    IChildSubflowFaultService childSubflowFaultService,
    ITransitionJobEnqueuer transitionJobEnqueuer,
    IInstanceCommandGateway instanceCommandGateway,
    IEventAppService eventAppService) : AetherControllerBase
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
        [FromBody] JsonElement? body,
        [FromQuery] string? version = null,
        [FromQuery] bool sync = false,
        [FromQuery] string[]? extensions = null,
        CancellationToken cancellationToken = default
    )
    {
        var httpContext = httpContextAccessor.HttpContext;
        var headers = httpContext?.Request.Headers ?? new HeaderDictionary();

        CreateInstanceDto request;
        if (PayloadModeDetector.IsStandard(headers, body))
        {
            request = body is null
                ? new CreateInstanceDto()
                : JsonSerializer.Deserialize<CreateInstanceDto>(body.Value, JsonSerializerOptions.Web) ?? new CreateInstanceDto();
        }
        else
        {
            request = new CreateInstanceDto { Attributes = body };
        }

        var input = new StartInstanceInput(domain, workflow, version, sync)
        {
            Instance = new CreateInstanceInput
            {
                Key = request.Key,
                Tags = request.Tags,
                Attributes = request.Attributes,
                Stage = request.Stage
            },
            Extensions = extensions
        };
        if (httpContext is not null)
        {
            input.Headers = httpContext.Request.Headers.ToDictionary(s => s.Key.ToLower(), s => s.Value.FirstOrDefault()?.ToString());
            input.RouteValues = httpContext.Request.RouteValues.ToDictionary(s => s.Key, s => s.Value?.ToString());
        }

        var result = await commandAppService.StartAsync(input, cancellationToken);
        return InstanceResponseActionResultMapper.ToActionResult(result, HttpContext);
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
                Stage = request.Stage,
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
        return WorkflowResultActionResultMapper.ToActionResult(result, HttpContext);
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
    /// Propagates SubFlow fault to parent instance.
    /// Internal endpoint for cross-domain SubFlow fault propagation.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/sub/fault")]
    public async Task<IActionResult> FaultSubAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromBody] SubFlowFaultedInput request,
        CancellationToken cancellationToken = default
    )
    {
        await subflowFaultService.FaultAsync(request, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Marks an instance Busy and recursively propagates to nested SubFlows.
    /// Internal endpoint for cross-domain SubFlow busy propagation.
    /// </summary>
    /// <param name="domain">Target workflow domain.</param>
    /// <param name="workflow">Target workflow definition key.</param>
    /// <param name="instance">Instance identifier (GUID).</param>
    /// <param name="version">Optional workflow version for schema resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Operation completed successfully or instance was absent (no-op).</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPut("{domain}/workflows/{workflow}/instances/{instance}/busy")]
    public async Task<IActionResult> MarkBusyAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var result = await instanceCommandGateway.MarkBusyAsync(
            new MarkBusyInput
            {
                Domain = domain,
                Workflow = workflow,
                InstanceId = instance,
                Version = version
            },
            cancellationToken);

        return FromResult(result);
    }

    /// <summary>
    /// Cancels scheduled jobs when an instance is canceled/completed/faulted.
    /// Internal endpoint the Inbox forwards canceled/completed-cleanup/faulted-cleanup events to.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/cancel-cleanup")]
    public async Task<IActionResult> CancelCleanupAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        CancellationToken cancellationToken = default)
    {
        var result = await cancellationService.ProcessCancellationAsync(instance, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Cancels a child subflow on request from its parent.
    /// Internal endpoint the Inbox forwards child-subflow-cancel events to.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/child-cancel")]
    public async Task<IActionResult> ChildCancelAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var result = await childSubflowCancellationService.CancelChildSubflowAsync(
            instance, domain, workflow, version, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Faults a child subflow on request from its parent.
    /// Internal endpoint the Inbox forwards child-subflow-fault events to.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/child-fault")]
    public async Task<IActionResult> ChildFaultAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] Guid parentInstanceId,
        CancellationToken cancellationToken = default)
    {
        var result = await childSubflowFaultService.FaultChildAsync(
            instance, domain, workflow, parentInstanceId, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Enqueues a (chained) transition as a background job. Internal endpoint the Inbox forwards
    /// <c>TransitionContinuationRequested</c> events to when outbox continuations are enabled, so
    /// the Dapr job is enqueued in the Orchestration process (never in the Inbox). Preserves the
    /// chain token for the chain-ownership gate.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey}/enqueue")]
    public async Task<IActionResult> EnqueueTransitionAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromRoute] string transitionKey,
        [FromBody] TransitionContinuationRequested continuation,
        CancellationToken cancellationToken = default)
    {
        var actor = Enum.TryParse<ExecutionActor>(continuation.ExecutionActor, ignoreCase: true, out var parsed)
            ? parsed
            : ExecutionActor.System;

        var payload = new TransitionJobPayload
        {
            JobName = continuation.JobName,
            InstanceId = continuation.InstanceId,
            TransitionKey = continuation.TransitionKey,
            Domain = continuation.Domain,
            Workflow = continuation.Flow,
            Version = continuation.Version,
            Data = continuation.Data,
            InstanceKey = continuation.InstanceKey,
            Tags = continuation.Tags,
            Stage = continuation.Stage,
            Headers = continuation.Headers,
            RouteValues = continuation.RouteValues,
            ExecutionActor = actor,
            CallerSync = false,
            TraceParent = continuation.TraceParent,
            TraceState = continuation.TraceState,
            ChainToken = continuation.ChainToken
        };

        await transitionJobEnqueuer.EnqueueAsync(payload, continuation.JobId, cancellationToken);
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
        [FromBody] JsonElement? body = null,
        [FromQuery] bool sync = false,
        [FromQuery] string[]? extensions = null,
        CancellationToken cancellationToken = default
    )
    {
        var httpContext = httpContextAccessor.HttpContext;
        var headers = httpContext?.Request.Headers ?? new HeaderDictionary();

        TransitionDataInput? data;
        if (body is null)
        {
            data = null;
        }
        else if (PayloadModeDetector.IsStandard(headers, body))
        {
            data = JsonSerializer.Deserialize<TransitionDataInput>(body.Value, JsonSerializerOptions.Web);
        }
        else
        {
            data = new TransitionDataInput(body);
        }

        var input = new TransitionInput(domain, workflow, data, sync)
        {
            Extensions = extensions
        };
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

        return InstanceResponseActionResultMapper.ToActionResult(result, HttpContext);
    }

    /// <summary>
    /// Acknowledges a long-poll termination signal and resumes the paused pipeline.
    /// The client calls this after it stops long polling and renders the entered-state screen.
    /// Idempotent: a no-op when the instance is not awaiting acknowledge (already resumed or the
    /// fallback timeout already fired).
    /// </summary>
    /// <response code="200">Acknowledge accepted (pipeline resumed or already resumed)</response>
    /// <response code="403">Acknowledge not permitted for the current roles</response>
    /// <response code="404">Instance or workflow not found</response>
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/longpoll/ack")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeLongPollAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] string? version = null,
        [FromQuery] string? role = null,
        CancellationToken cancellationToken = default)
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers
            .ToDictionary(s => s.Key.ToLower(), s => s.Value.FirstOrDefault()?.ToString()) ?? [];

        var input = new AcknowledgeLongPollInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version,
            Role = role,
            Headers = headers
        };

        var result = await commandAppService.AcknowledgeLongPollAsync(input, cancellationToken);
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
        return WorkflowResultActionResultMapper.ToActionResult(result, HttpContext);
    }

    /// <summary>
    /// Receives an external event (delivered by a domain-owned subscription / input binding) and turns
    /// it into a workflow action: starting a new instance or advancing an existing one via a transition.
    /// Internal integration endpoint — hidden from the public API surface.
    /// </summary>
    /// <param name="domain">Target domain.</param>
    /// <param name="workflow">Target workflow key.</param>
    /// <param name="action">Either <c>start</c> (create a new instance) or <c>transition</c> (advance an existing one).</param>
    /// <param name="transitionKey">Transition to execute. Required when <paramref name="action"/> is <c>transition</c>.</param>
    /// <param name="payload">Raw event payload, mapped by the workflow's/transition's event mapping.</param>
    /// <param name="sync">When true, blocks until the pipeline completes; otherwise accepted and run asynchronously.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <response code="200">Event processed (or intentionally ignored when no active instance matches).</response>
    /// <response code="400">Invalid action, or transitionKey missing for action=transition.</response>
    /// <response code="404">Workflow or event definition not found.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/events")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> HandleEventAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string action,
        [FromQuery] string? transitionKey = null,
        [FromBody] JsonElement payload = default,
        [FromQuery] bool sync = false,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<EventAction>(action, ignoreCase: true, out var eventAction))
            return BadRequest(new ProblemDetails
            {
                Title = "Invalid action",
                Detail = $"action must be 'start' or 'transition'. Received: '{action}'.",
                Status = StatusCodes.Status400BadRequest
            });

        var input = new EventInput
        {
            Domain = domain,
            Workflow = workflow,
            Action = eventAction,
            TransitionKey = transitionKey,
            Payload = UnwrapCloudEvent(payload),
            Sync = sync,
            Headers = HttpContext.Request.Headers
                .ToDictionary(h => h.Key.ToLower(), h => h.Value.FirstOrDefault())
        };

        var result = await eventAppService.HandleAsync(input, cancellationToken);
        return WorkflowResultActionResultMapper.ToActionResult(result, HttpContext);
    }

    /// <summary>
    /// When the event endpoint is fed by a Dapr pub/sub subscription, the message arrives as a
    /// structured CloudEvent envelope (<c>{ specversion, id, source, type, data, ... }</c>) rather than
    /// the raw domain payload. Unwrap the inner <c>data</c> so the event mapping sees the same shape it
    /// would for a direct caller. Bodies that are not CloudEvents are passed through unchanged.
    /// </summary>
    private static JsonElement UnwrapCloudEvent(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty("specversion", out _)
           && payload.TryGetProperty("data", out var data)
            ? data.Clone()
            : payload;

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
    /// <param name="domain">Target domain (route).</param>
    /// <param name="workflow">Target workflow key (route).</param>
    /// <param name="filter">Filter JSON: a plain GraphQL node (e.g. {"attributes":{"status":{"eq":"active"}}}) or a request envelope embedding groupBy/aggregations. groupBy/aggregations may also arrive as separate query parameters.</param>
    /// <param name="extensions">Extensions requested for instance data enrichment.</param>
    /// <param name="page">Page number for pagination (1-based).</param>
    /// <param name="pageSize">Page size for pagination.</param>
    /// <param name="sort">OrderBy JSON: single {"field":"createdAt","direction":"desc"} or multiple {"fields":[{"field":"status","direction":"asc"},{"field":"createdAt","direction":"desc"}]}. Also accepted as orderBy.</param>
    /// <param name="orderBy">Alias for sort; same JSON format. If both provided, orderBy wins.</param>
    /// <param name="version">Optional instance data version; latest data is used when empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
            Filter = filter,
            Sort = orderBy ?? sort,
            Version = version,
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var response = await queryAppService.GetInstanceListAsync(input, cancellationToken);
        return response.ToActionResult(HttpContext);
    }

    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/transitions")]
    public async Task<IActionResult> GetInstanceHistoryAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var input = new GetInstanceHistoryInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
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
