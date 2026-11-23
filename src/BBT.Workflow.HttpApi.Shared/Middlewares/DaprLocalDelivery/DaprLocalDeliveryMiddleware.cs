using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Lets Dapr-triggered endpoints accept only deliveries from the domain's own sidecar. An endpoint
/// is guarded when it carries <see cref="DaprLocalDeliveryOnlyAttribute"/> or is the Dapr jobs
/// callback route; the decision is by endpoint, never by path. The <c>dapr-api-token</c> header is
/// removed from every request so it never reaches application code. Rejection is a bodiless 403, never 404:
/// Dapr pub/sub drops a message for good on 404 and retries on any other status, so a misconfigured
/// token on our own sidecar must stay a retryable failure rather than a silent loss.
/// </summary>
public sealed class DaprLocalDeliveryMiddleware
{
    internal const string JobRoutePattern = "/job/{jobName}";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<DaprLocalDeliveryOptions> _options;
    private readonly DaprAppApiToken _token;
    private readonly ILogger<DaprLocalDeliveryMiddleware> _logger;

    public DaprLocalDeliveryMiddleware(
        RequestDelegate next,
        IOptionsMonitor<DaprLocalDeliveryOptions> options,
        DaprAppApiToken token,
        ILogger<DaprLocalDeliveryMiddleware> logger)
    {
        _next = next;
        _options = options;
        _token = token;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var rejected = false;
        var mode = IsGuarded(context.GetEndpoint()) ? CurrentMode() : DaprLocalDeliveryMode.Off;

        if (mode != DaprLocalDeliveryMode.Off)
        {
            var verdict = DaprLocalDeliveryEvaluator.Evaluate(context.Request.Headers, _token.Value);
            // LogOnly without a token is announced once at startup (DaprLocalDeliveryStartupCheck); per-request entries would only
            // repeat it for every pubsub, cron and Dapr Scheduler delivery.
            var silent = verdict == DaprLocalDeliveryVerdict.TokenNotConfigured
                         && mode == DaprLocalDeliveryMode.LogOnly;
            if (verdict != DaprLocalDeliveryVerdict.Accepted && !silent)
            {
                _logger.DaprLocalDeliveryRejected(
                    mode.ToString(),
                    verdict.ToString(),
                    context.Request.Path.Value,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers[DaprLocalDeliveryEvaluator.CallerAppIdHeader].FirstOrDefault());
                rejected = mode == DaprLocalDeliveryMode.Enforce;
            }
        }

        context.Request.Headers.Remove(DaprLocalDeliveryEvaluator.TokenHeader);

        if (rejected)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }

    /// <summary>
    /// The configured mode. Enforce without a token file fails startup
    /// (<see cref="DaprLocalDeliveryOptionsValidator"/>); a later configuration reload into that state
    /// makes the options invalid, so the read throws. That is the only invalid combination, so the guard
    /// fails closed as Enforce and logs EventId 40997 rather than letting the exception fail the request.
    /// </summary>
    private DaprLocalDeliveryMode CurrentMode()
    {
        try
        {
            return _options.CurrentValue.Mode;
        }
        catch (OptionsValidationException)
        {
            _logger.DaprLocalDeliveryEnforcedWithoutToken();
            return DaprLocalDeliveryMode.Enforce;
        }
    }

    private static bool IsGuarded(Endpoint? endpoint) =>
        endpoint is not null
        && (endpoint.Metadata.GetMetadata<DaprLocalDeliveryOnlyAttribute>() is not null
            || (endpoint is RouteEndpoint route && route.RoutePattern.RawText == JobRoutePattern));
}
