using System.Text.Json;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Events;
using BBT.Workflow.HttpApi.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Controllers.Events;

/// <summary>
/// Receives external events (delivered by a domain-owned subscription / input binding) and turns them
/// into a workflow action: starting a new instance or advancing an existing one via a transition.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class EventController(IEventAppService eventAppService) : AetherControllerBase
{
    /// <summary>
    /// Handles an inbound event for a workflow.
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
    [HttpPost("{domain}/workflows/{workflow}/events")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> HandleAsync(
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
    /// When this endpoint is fed by a Dapr pub/sub subscription, the message arrives as a structured
    /// CloudEvent envelope (<c>{ specversion, id, source, type, data, ... }</c>) rather than the raw
    /// domain payload. Unwrap the inner <c>data</c> so the event mapping sees the same shape it would
    /// for a direct caller. Bodies that are not CloudEvents are passed through unchanged.
    /// </summary>
    private static JsonElement UnwrapCloudEvent(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty("specversion", out _)
           && payload.TryGetProperty("data", out var data)
            ? data.Clone()
            : payload;
}
