using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Middleware that reads <c>X-Parent-Instance-Id</c> and <c>X-Root-Instance-Id</c> request headers
/// (when present) and adds them to the current Activity (tag and baggage) and to the log scope,
/// so that traces and logs for subflow/subprocess requests are searchable by parent and root instance ID.
/// Should be registered after UseCorrelationId() and before controllers.
/// </summary>
public sealed class ParentInstanceIdEnrichmentMiddleware(RequestDelegate next, ILogger<ParentInstanceIdEnrichmentMiddleware> logger)
{
    /// <summary>
    /// Reads the parent and root instance ID headers, enriches Activity and log scope when present,
    /// then invokes the next middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var parentInstanceId = context.Request.Headers[TelemetryConstants.HeaderNames.ParentInstanceId].FirstOrDefault();
        var rootInstanceId   = context.Request.Headers[TelemetryConstants.HeaderNames.RootInstanceId].FirstOrDefault();

        var activity = Activity.Current;
        var scopeProperties = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(parentInstanceId))
        {
            activity?.SetTag(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
            activity?.SetBaggage(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
            scopeProperties[TelemetryConstants.TagNames.ParentInstanceId] = parentInstanceId;
        }

        if (!string.IsNullOrEmpty(rootInstanceId))
        {
            activity?.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId);
            activity?.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId);
            scopeProperties[TelemetryConstants.TagNames.RootInstanceId] = rootInstanceId;
        }

        if (scopeProperties.Count == 0)
        {
            await next(context);
            return;
        }

        using (logger.BeginScope(scopeProperties))
        {
            await next(context);
        }
    }
}
