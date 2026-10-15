using System.Collections.Generic;
using BBT.Aether.Results;
using BBT.Workflow.Events;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.HttpApi.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Schedule;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Orchestration.Controllers.Instances;

/// <summary>
/// Maps scheduled-start results to Dapr-compatible responses for
/// <c>POST /{domain}/workflows/{workflow}/instances/schedule</c>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="EventDeliveryResultMapper"/> and reuses <see cref="EventDeliveryResponse"/>
/// rather than returning an instance envelope. Dapr's cron binding happens to discard the response
/// entirely, but the body still has to be safe for the general case: this endpoint is reachable by
/// any Dapr caller, and an instance DTO's <c>status</c> field (<c>"A"</c>, <c>"B"</c>, …) is read by
/// Dapr as a protocol signal it does not recognise, which causes endless redelivery.
/// </para>
/// <para>
/// The one behavioural difference from event delivery is what "permanent" means. A cron tick is never
/// redelivered, so the <c>DROP</c>/non-2xx distinction cannot change the outcome of <i>this</i> tick.
/// It is kept anyway because it changes the log level and the operator's signal: a misconfigured
/// component repeating a validation failure every 15 minutes should read as a configuration defect,
/// not as a transient outage.
/// </para>
/// </remarks>
internal static class ScheduledStartResultMapper
{
    /// <summary>
    /// Error prefixes a later tick could never resolve on its own — the component, the workflow
    /// definition or the seed data is wrong.
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
    /// Translates the schedule service result into a Dapr-compatible response.
    /// </summary>
    /// <param name="result">Outcome of <see cref="IInstanceScheduleAppService.StartAsync"/>.</param>
    /// <param name="input">The tick, used for log context.</param>
    /// <param name="httpContext">Current request context (used for the failure passthrough).</param>
    /// <param name="logger">Logger for the drop warning.</param>
    internal static IActionResult ToActionResult(
        Result<object?> result,
        ScheduledStartInput input,
        HttpContext httpContext,
        ILogger logger)
    {
        if (result.IsSuccess)
        {
            // Identity is echoed only for sync callers, matching the event endpoint. A replica that
            // lost the race sees the instance the winner created — indistinguishable by design.
            var instance = input.Sync ? ToInstance(result.Value) : null;
            return new OkObjectResult(EventDeliveryResponse.Succeeded(instance));
        }

        if (PermanentErrorPrefixes.Contains(result.Error.Prefix))
        {
            var reason = $"{result.Error.Code}: {result.Error.Message}";
            logger.EventDeliveryDropped(
                input.Domain, input.Workflow, transitionKey: null, result.Error.Code, reason);
            return new OkObjectResult(EventDeliveryResponse.Dropped(reason));
        }

        // Transient failure. Non-2xx is the honest answer even though nothing will retry it.
        return WorkflowResultActionResultMapper.ToActionResult(result, httpContext);
    }

    private static EventDeliveryInstance? ToInstance(object? value)
        => value is InstanceOutputBase output
            ? new EventDeliveryInstance(output.Id, output.Key, output.Status?.Code)
            : null;
}
