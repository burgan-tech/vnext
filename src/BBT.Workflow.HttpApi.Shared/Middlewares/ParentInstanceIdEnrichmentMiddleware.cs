using System.Diagnostics;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Middleware that reads the X-Parent-Instance-Id request header (when present) and adds it to
/// the current Activity (tag and baggage) and to the log scope so that traces and logs for
/// subflow/subprocess requests are searchable by parent instance ID.
/// Should be registered after UseCorrelationId() and before controllers.
/// </summary>
public sealed class ParentInstanceIdEnrichmentMiddleware(RequestDelegate next, ILogger<ParentInstanceIdEnrichmentMiddleware> logger)
{
    /// <summary>
    /// Reads the parent instance ID header, enriches Activity and log scope when present, then invokes the next middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var parentInstanceId = context.Request.Headers[TelemetryConstants.HeaderNames.ParentInstanceId].FirstOrDefault();

        if (string.IsNullOrEmpty(parentInstanceId))
        {
            await next(context);
            return;
        }

        // Add to current activity for trace correlation
        var activity = Activity.Current;
        if (activity != null)
        {
            activity.SetTag(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
            activity.SetBaggage(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
        }

        // Add to log scope so all logs during this request include ParentInstanceId for search
        using (logger.BeginScope(new Dictionary<string, object> { [TelemetryConstants.TagNames.ParentInstanceId] = parentInstanceId }))
        {
            await next(context);
        }
    }
}
