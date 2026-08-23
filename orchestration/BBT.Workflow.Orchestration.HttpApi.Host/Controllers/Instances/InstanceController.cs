using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using BBT.Aether;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Results;
using BBT.Aether.Users;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Events;
using BBT.Workflow.Gateway;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Shared;
using BBT.Workflow.SubFlow;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using BBT.Workflow.Logging;

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
    ISubflowCancellationService subflowCancellationService,
    IInstanceCancellationService cancellationService,
    IChildSubflowCancellationService childSubflowCancellationService,
    IChildSubflowFaultService childSubflowFaultService,
    IInstanceCommandGateway instanceCommandGateway,
    IEventAppService eventAppService,
    IRelatedInstanceQueryAppService relatedInstanceQueryAppService,
    ICurrentUser currentUser) : AetherControllerBase
{
    /// <summary>
    /// Starts a new workflow instance.
    /// </summary>
    /// <response code="200">Instance started synchronously (sync=true)</response>
    /// <response code="202">Instance accepted for durable background processing (sync=false)</response>
    /// <response code="400">Validation failed</response>
    /// <response code="404">Workflow or state not found</response>
    /// <response code="409">Instance with same key already exists</response>
    [HttpPost("{domain}/workflows/{workflow}/instances/start")]
    [ProducesResponseType(typeof(StartInstanceOutput), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(StartInstanceOutput), StatusCodes.Status202Accepted)]
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
        return InstanceResponseActionResultMapper.ToActionResult(result, HttpContext, async: !sync);
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
        // Adopt the completing subflow's lane, overriding the anchor the request middleware set to
        // this relay endpoint's server span. ParentTraceRoot is what puts the resume back at the
        // parent instance's level in the originating request's trace.
        using var lane = WorkflowTraceLane.Reset(request.TraceRoot, request.ParentTraceRoot);

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
        // Adopt the faulting subflow's lane so the parent resume lands at the parent instance's
        // level, not nested under this relay endpoint's server span.
        using var lane = WorkflowTraceLane.Reset(request.TraceRoot, request.ParentTraceRoot);

        await subflowFaultService.FaultAsync(request, cancellationToken);
        return Ok();
    }

    /// <summary>
    /// Propagates a canceled SubItem outcome to its parent instance.
    /// Internal endpoint for cross-domain cancellation propagation.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/sub/cancel")]
    public async Task<IActionResult> CancelSubAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromBody] SubItemCanceledInput request,
        CancellationToken cancellationToken = default)
    {
        await subflowCancellationService.CancellationAsync(request, cancellationToken);
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
    /// Relays a transition from a parent instance to an active SubFlow in this domain.
    /// Internal-only counterpart of the public transition endpoint: the chain-reserve claim
    /// travels in the request body, because the public endpoint copies caller headers unfiltered
    /// and a header-borne claim would let any client bypass the Busy 409. Protected by network
    /// isolation, like the related-data endpoints.
    /// </summary>
    /// <param name="domain">Target workflow domain.</param>
    /// <param name="workflow">Target workflow definition key.</param>
    /// <param name="instance">Target SubFlow instance identifier (GUID).</param>
    /// <param name="transitionKey">Transition key to relay.</param>
    /// <param name="input">Forwarded transition payload and the chain-reserve claim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Relay executed (sync) or accepted (async).</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/{instance}/internal/subflow-forward")]
    public async Task<IActionResult> SubflowForwardAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] string transitionKey,
        [FromBody] SubflowForwardInput input,
        CancellationToken cancellationToken = default)
    {
        var transitionInput = new TransitionInput(
            domain,
            workflow,
            new TransitionDataInput(input.Attributes)
            {
                Key = input.Key,
                Tags = input.Tags,
                Stage = input.Stage
            },
            input.Sync)
        {
            RouteValues = input.RouteValues,
            CorrelationId = input.CorrelationId,
            ChainReserved = input.ChainReserved
        };

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is not null)
        {
            transitionInput.Headers = httpContext.Request.Headers
                .ToDictionary(s => s.Key.ToLower(), s => s.Value.FirstOrDefault()?.ToString());
        }

        // Adopt the forwarding parent's lane for this relay, overriding the anchor the request
        // middleware set to THIS endpoint's server span. Without it the subflow's hops would anchor
        // on the relay endpoint and detach from the originating request's trace tree.
        using var lane = WorkflowTraceLane.Reset(input.TraceRoot, input.ParentTraceRoot);

        var result = await commandAppService.TransitionAsync(
            instance.ToString(),
            transitionKey,
            transitionInput,
            cancellationToken);

        return InstanceResponseActionResultMapper.ToActionResult(result, HttpContext, async: !input.Sync);
    }

    /// <summary>
    /// Releases an accept-time SubFlow chain reserve, recursively propagating to nested SubFlows.
    /// Internal-only compensation endpoint — the mirror of <see cref="MarkBusyAsync"/>. Levels
    /// holding an open SubFlow correlation are Busy by design and are recursed past, not released.
    /// </summary>
    /// <param name="domain">Target workflow domain.</param>
    /// <param name="workflow">Target workflow definition key.</param>
    /// <param name="instance">Instance identifier (GUID).</param>
    /// <param name="version">Optional workflow version for schema resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <response code="200">Operation completed successfully or instance was absent (no-op).</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPut("{domain}/workflows/{workflow}/instances/{instance}/internal/busy-release")]
    public async Task<IActionResult> ReleaseBusyAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        var result = await instanceCommandGateway.ReleaseBusyAsync(
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
        [FromBody] ChildSubflowCancelInput request,
        CancellationToken cancellationToken = default)
    {
        var result = await childSubflowCancellationService.CancelChildSubflowAsync(
            instance, domain, workflow, request.Version, request.Termination, cancellationToken);
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
        [FromBody] ChildSubflowFaultInput request,
        CancellationToken cancellationToken = default)
    {
        var result = await childSubflowFaultService.FaultChildAsync(
            instance, domain, workflow, request.ParentInstanceId, request.Termination, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Reads a single instance's raw data for related-instance access from another runtime.
    /// Internal-to-internal: no caller identity, no query-role check, no x-roles field filtering and
    /// no extensions. Never expose this route publicly.
    /// </summary>
    /// <response code="200">The instance snapshot.</response>
    /// <response code="204">No such instance — absence, not an error.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/internal/related-data")]
    public async Task<IActionResult> GetRelatedDataAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromRoute] Guid instance,
        [FromQuery] string? version,
        CancellationToken cancellationToken = default)
    {
        var result = await relatedInstanceQueryAppService.ReadAsync(
            new RelatedInstanceRef(instance, domain, workflow, version),
            cancellationToken);

        // FromResult is the house pattern for this controller. A successful read of a nonexistent
        // instance maps to 204 No Content — deliberately NOT 404, which would be indistinguishable
        // from a misrouted request or a wrong app id. Absence is data; a wrong route is a fault.
        return FromResult(result);
    }

    /// <summary>
    /// Reads several instances' raw data in one call for related-instance access from another runtime.
    /// Internal-to-internal, same caveats as the single read. Ids that do not resolve are omitted.
    /// </summary>
    /// <response code="200">The resolved instance snapshots (possibly an empty array).</response>
    /// <response code="400">More ids were requested than <see cref="RelatedDataBatchInput.MaxInstanceIds"/> allows.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/internal/related-data/batch")]
    public async Task<IActionResult> GetRelatedDataBatchAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromBody] RelatedDataBatchInput input,
        [FromQuery] string? version,
        CancellationToken cancellationToken = default)
    {
        // Defence in depth: this endpoint carries no authorization, so it must not trust the caller's
        // batch size. The real cap lives in the calling runtime and cannot be enforced from here.
        if (input.InstanceIds.Count > RelatedDataBatchInput.MaxInstanceIds)
            return BadRequest(
                $"At most {RelatedDataBatchInput.MaxInstanceIds} instance ids may be read in one batch.");

        var references = input.InstanceIds
            .Select(id => new RelatedInstanceRef(id, domain, workflow, version))
            .ToList();

        var result = await relatedInstanceQueryAppService.ReadManyAsync(references, cancellationToken);

        return FromResult(result);
    }
    /// <summary>
    /// Executes a transition on a workflow instance.
    /// </summary>
    /// <response code="200">Transition executed synchronously (sync=true)</response>
    /// <response code="202">Transition accepted for durable background processing (sync=false)</response>
    /// <response code="400">Validation or state transition rule failed</response>
    /// <response code="403">Transition not authorized for current context</response>
    /// <response code="404">Instance, workflow, or transition not found</response>
    /// <response code="409">Transition already in progress (locked) or SubFlow blocking</response>
    /// <response code="503">Service temporarily unavailable</response>
    [HttpPatch("{domain}/workflows/{workflow}/instances/{instance}/transitions/{transitionKey}")]
    [ProducesResponseType(typeof(TransitionOutput), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(TransitionOutput), StatusCodes.Status202Accepted)]
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

        return InstanceResponseActionResultMapper.ToActionResult(result, HttpContext, async: !sync);
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
    /// <param name="sync">When true, blocks until the pipeline completes; otherwise accepted and run asynchronously.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <remarks>
    /// <para>
    /// The raw event payload is read directly from the request body and parsed as JSON, independent of the
    /// request <c>Content-Type</c>. Kafka / pub-sub sources routed through Dapr routinely deliver bodies with
    /// no content type or <c>application/octet-stream</c>; binding via <c>[FromBody]</c> would reject those with
    /// <c>415 Unsupported Media Type</c>, so the body is bound manually here instead.
    /// </para>
    /// <para>
    /// The response is an <see cref="EventDeliveryResponse"/> — a Dapr pub/sub protocol body, not an
    /// instance envelope. Dapr reads the top-level <c>status</c> field as its delivery signal, so
    /// returning an instance DTO here (whose <c>status</c> is an <see cref="InstanceStatus"/> code such
    /// as <c>"B"</c>) makes Dapr treat every delivery as failed and redeliver the same message forever,
    /// blocking the partition. Unprocessable messages are answered with <c>200 DROP</c> for the same
    /// reason; only transient failures return non-2xx so the broker retries them.
    /// </para>
    /// </remarks>
    /// <response code="200">Dapr signal: <c>SUCCESS</c> when processed (or intentionally ignored because no active instance matches), <c>DROP</c> when the message can never be processed.</response>
    /// <response code="500">Transient failure — the broker should redeliver.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("{domain}/workflows/{workflow}/instances/events")]
    [ProducesResponseType(typeof(EventDeliveryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> HandleEventAsync(
        [FromRoute] string domain,
        [FromRoute] string workflow,
        [FromQuery] string action,
        [FromQuery] string? transitionKey = null,
        [FromQuery] bool sync = false,
        CancellationToken cancellationToken = default)
    {
        // A subscription routed at the wrong action, or a producer publishing a body that is not JSON,
        // is a permanent defect: retrying it can never succeed, so drop instead of wedging the topic.
        if (!Enum.TryParse<EventAction>(action, ignoreCase: true, out var eventAction))
            return EventDeliveryResultMapper.Drop(
                $"InvalidEventAction: action must be 'start' or 'transition'. Received: '{action}'.",
                domain, workflow, transitionKey, "InvalidEventAction", Logger);

        JsonElement payload;
        try
        {
            payload = await ReadEventPayloadAsync(HttpContext.Request, cancellationToken);
        }
        catch (JsonException exception)
        {
            return EventDeliveryResultMapper.Drop(
                $"InvalidEventPayload: the request body could not be parsed as JSON: {exception.Message}",
                domain, workflow, transitionKey, "InvalidEventPayload", Logger);
        }

        var input = new EventInput
        {
            Domain = domain,
            Workflow = workflow,
            Action = eventAction,
            TransitionKey = transitionKey,
            Payload = payload,
            Sync = sync,
            Headers = HttpContext.Request.Headers
                .ToDictionary(h => h.Key.ToLower(), h => h.Value.FirstOrDefault())
        };

        var result = await eventAppService.HandleAsync(input, cancellationToken);
        return EventDeliveryResultMapper.ToActionResult(result, input, HttpContext, Logger);
    }

    /// <summary>
    /// Reads the event request body as raw bytes and parses it into a <see cref="JsonElement"/>, ignoring the
    /// request <c>Content-Type</c>. An empty body yields a default (<see cref="JsonValueKind.Undefined"/>) element,
    /// preserving the previous <c>[FromBody]</c> optional-body behaviour. Malformed JSON surfaces as
    /// <see cref="JsonException"/> for the caller to translate into a 400 response.
    /// </summary>
    internal static async Task<JsonElement> ReadEventPayloadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);

        if (buffer.Length == 0)
            return default;

        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        return document.RootElement.Clone();
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
            QueryParameters = requestContext.QueryParameters,
            // Without this the queryRoles gate evaluates a role-less caller, so this route would
            // disagree with the `data` function handler about the very same instance.
            Roles = currentUser.ResolveCallerRoles(requestContext.Headers)
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

public sealed record ChildSubflowFaultInput(
    Guid ParentInstanceId,
    TerminationContext Termination);
