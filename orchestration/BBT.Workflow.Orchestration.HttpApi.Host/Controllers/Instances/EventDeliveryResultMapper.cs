using BBT.Aether.Results;
using BBT.Workflow.Events;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Maps event-delivery results to Dapr pub/sub protocol responses for
/// <c>POST /{domain}/workflows/{workflow}/instances/events</c>.
/// </summary>
/// <remarks>
/// <para>
/// Dapr reads the top-level <c>status</c> field of the response body as a protocol signal
/// (<c>SUCCESS</c> / <c>RETRY</c> / <c>DROP</c>), so this endpoint cannot return the instance DTOs
/// the rest of <c>InstanceController</c> returns — their <c>status</c> property carries an
/// <see cref="InstanceStatus"/> code, which Dapr rejects and then redelivers forever.
/// Everything here therefore goes out as an <see cref="EventDeliveryResponse"/>.
/// </para>
/// <para>
/// Failures are split by intent rather than mapped uniformly:
/// permanently unprocessable messages (bad <c>transitionKey</c>, missing event definition, wrong
/// domain, malformed body) are <c>DROP</c>ped with <c>200</c> so a single poison message cannot wedge
/// a partition, while transient failures keep their non-2xx <c>ProblemDetails</c> response — Dapr
/// already redelivers on non-2xx, and the error stays visible to metrics, traces and alerts.
/// Bounding those retries is the subscription's job (<c>maxRetries</c> resiliency policy plus a
/// <c>deadLetterTopic</c>).
/// </para>
/// </remarks>
internal static class EventDeliveryResultMapper
{
    /// <summary>
    /// Error prefixes whose failures can never be resolved by redelivering the same message —
    /// configuration or payload defects rather than infrastructure hiccups.
    /// </summary>
    private static readonly HashSet<string> PermanentErrorPrefixes =
    [
        ErrorCodes.Prefixes.Validation,
        ErrorCodes.Prefixes.NotFound,
        ErrorCodes.Prefixes.NotSupported,
        ErrorCodes.Prefixes.Unauthorized,
        ErrorCodes.Prefixes.Forbidden
    ];

    /// <summary>
    /// Translates the event service result into a Dapr-compatible response.
    /// </summary>
    /// <param name="result">Outcome of <c>IEventAppService.HandleAsync</c>.</param>
    /// <param name="input">The event request, used for log context.</param>
    /// <param name="httpContext">Current request context (used for the failure passthrough).</param>
    /// <param name="logger">Logger for the drop warning.</param>
    internal static IActionResult ToActionResult(
        Result<object?> result,
        EventInput input,
        HttpContext httpContext,
        ILogger logger)
    {
        if (result.IsSuccess)
        {
            // A null value is the deliberate "no active instance matched" acknowledgement: the event
            // evaporates instead of being redelivered.
            var instance = input.Sync ? ToInstance(result.Value) : null;
            return new OkObjectResult(EventDeliveryResponse.Succeeded(instance));
        }

        if (PermanentErrorPrefixes.Contains(result.Error.Prefix))
            return Drop(
                $"{result.Error.Code}: {result.Error.Message}",
                input.Domain,
                input.Workflow,
                input.TransitionKey,
                result.Error.Code,
                logger);

        // Transient / infrastructure failure: keep the ProblemDetails response. Non-2xx already means
        // "retry" to Dapr, so no body-level RETRY signal is needed.
        return WorkflowResultActionResultMapper.ToActionResult(result, httpContext);
    }

    /// <summary>
    /// Builds a <c>200 DROP</c> response for a delivery that can never succeed, logging why so the
    /// discarded message remains diagnosable even though the status code is a success.
    /// </summary>
    internal static IActionResult Drop(
        string reason,
        string? domain,
        string? workflow,
        string? transitionKey,
        string? errorCode,
        ILogger logger)
    {
        logger.EventDeliveryDropped(domain, workflow, transitionKey, errorCode, reason);
        return new OkObjectResult(EventDeliveryResponse.Dropped(reason));
    }

    private static EventDeliveryInstance? ToInstance(object? value)
        => value is InstanceOutputBase output
            ? new EventDeliveryInstance(output.Id, output.Key, output.Status?.Code)
            : null;
}
