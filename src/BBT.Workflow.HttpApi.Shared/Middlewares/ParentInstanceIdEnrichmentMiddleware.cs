using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Tracing;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Middleware that reads <c>X-Parent-Instance-Id</c> and <c>X-Root-Instance-Id</c> request headers
/// (when present) and adds them to the current Activity (tag and baggage) and to the log scope,
/// so that traces and logs for subflow/subprocess requests are searchable by parent and root instance ID.
/// It also tags the ASP.NET Core server span with the originating request id.
/// Must be registered after UseCorrelationId() and before controllers.
/// <para>
/// It additionally establishes the request's <see cref="WorkflowTraceLane"/> anchor. This middleware
/// is the right place because it already runs while <c>Activity.Current</c> IS the ASP.NET Core
/// server span — the span that must become the common parent of every top-level operation of the
/// request (each transition hop, each post-commit job), so they render as siblings under the APM
/// transaction instead of nesting one inside the other.
/// </para>
/// </summary>
public sealed class ParentInstanceIdEnrichmentMiddleware(
    RequestDelegate next,
    ICorrelationIdProvider correlationIdProvider,
    ILogger<ParentInstanceIdEnrichmentMiddleware> logger)
{
    /// <summary>
    /// Tags the server span with the request id, reads the parent and root instance ID headers,
    /// enriches Activity and log scope when present, then invokes the next middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var parentInstanceId = context.Request.Headers[TelemetryConstants.HeaderNames.ParentInstanceId].FirstOrDefault();
        var rootInstanceId   = context.Request.Headers[TelemetryConstants.HeaderNames.RootInstanceId].FirstOrDefault();

        // Continue the read ladder this request is a level of. Seeded before anything downstream can
        // descend further, so the next Subflow.Descend numbers itself relative to the caller's depth
        // rather than restarting at 1. Unparseable or absent ⇒ left at 0 (Seed ignores non-positive).
        var subflowDepth = context.Request.Headers[TelemetryConstants.HeaderNames.SubflowDepth].FirstOrDefault();
        if (int.TryParse(subflowDepth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth))
            SubflowDescentContext.Seed(depth);

        var activity = Activity.Current;
        var scopeProperties = new Dictionary<string, object>();

        // The ASP.NET Core server span starts before UseCorrelationId(), so RequestIdSpanProcessor
        // cannot see the correlation scope at its OnStart. This middleware runs right after that
        // scope is opened and Activity.Current is still the server span, so it closes the gap.
        // Read from the provider, not the header, so the value has a single source.
        var requestId = correlationIdProvider.Get();
        if (!string.IsNullOrEmpty(requestId))
        {
            activity?.SetTag(TelemetryConstants.TagNames.RequestId, requestId);
        }

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

        // Anchor the trace lane on the server span for the whole request. Deliberately NOT read
        // from a request header: a caller-supplied anchor would let anyone graft their spans onto an
        // unrelated trace. Cross-service lane hand-off travels in internal-only request bodies
        // (SubflowForwardInput, FlowCompletedInput) and job payloads instead.
        using var lane = WorkflowTraceLane.UseCurrentActivity();

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
