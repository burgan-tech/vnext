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
/// removed from every request so it never reaches application code. Rejection is a bodiless 404.
/// </summary>
public sealed class DaprLocalDeliveryMiddleware
{
    internal const string JobRoutePattern = "/job/{jobName}";

    private readonly RequestDelegate _next;
    private readonly IOptionsMonitor<DaprLocalDeliveryOptions> _options;
    private readonly ILogger<DaprLocalDeliveryMiddleware> _logger;

    public DaprLocalDeliveryMiddleware(
        RequestDelegate next,
        IOptionsMonitor<DaprLocalDeliveryOptions> options,
        ILogger<DaprLocalDeliveryMiddleware> logger)
    {
        _next = next;
        _options = options;
        _logger = logger;

        var current = options.CurrentValue;
        if (current.Mode == DaprLocalDeliveryMode.Enforce && string.IsNullOrWhiteSpace(current.AppApiToken))
        {
            _logger.DaprLocalDeliveryEnforcedWithoutToken();
        }
        else if (current.Mode == DaprLocalDeliveryMode.LogOnly && string.IsNullOrWhiteSpace(current.AppApiToken))
        {
            _logger.DaprLocalDeliveryLogOnlyWithoutToken();
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var options = _options.CurrentValue;
        var rejected = false;

        if (options.Mode != DaprLocalDeliveryMode.Off && IsGuarded(context.GetEndpoint()))
        {
            var verdict = DaprLocalDeliveryEvaluator.Evaluate(context.Request.Headers, options.AppApiToken);
            // LogOnly without a token is announced once at construction; per-request entries would only
            // repeat it for every pubsub, cron and Dapr Scheduler delivery.
            var silent = verdict == DaprLocalDeliveryVerdict.TokenNotConfigured
                         && options.Mode == DaprLocalDeliveryMode.LogOnly;
            if (verdict != DaprLocalDeliveryVerdict.Accepted && !silent)
            {
                _logger.DaprLocalDeliveryRejected(
                    options.Mode.ToString(),
                    verdict.ToString(),
                    context.Request.Path.Value,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers[DaprLocalDeliveryEvaluator.CallerAppIdHeader].FirstOrDefault());
                rejected = options.Mode == DaprLocalDeliveryMode.Enforce;
            }
        }

        context.Request.Headers.Remove(DaprLocalDeliveryEvaluator.TokenHeader);

        if (rejected)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }

    private static bool IsGuarded(Endpoint? endpoint) =>
        endpoint is not null
        && (endpoint.Metadata.GetMetadata<DaprLocalDeliveryOnlyAttribute>() is not null
            || (endpoint is RouteEndpoint route && route.RoutePattern.RawText == JobRoutePattern));
}
