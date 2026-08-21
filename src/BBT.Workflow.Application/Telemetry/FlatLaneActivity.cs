using System.Diagnostics;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Starts the <em>flat-lane</em> spans of a business request — the top-level operations that must all
/// sit at the same depth instead of nesting inside one another.
/// <para>
/// This type is the single home of the lane parenting policy. Everything that opts into a lane
/// (<c>TransitionJob.Execute</c>, <c>PostCommit.*</c>, <c>SubFlow.Resume</c>) goes through
/// <see cref="Start"/>, so there is exactly one place where "which span is my parent" is decided and
/// exactly one place to review. See <see cref="WorkflowTraceLane"/> for the lane model.
/// </para>
/// <para>
/// The predecessor (hop N when starting hop N+1) is <b>not</b> discarded: it is attached as an
/// <see cref="ActivityLink"/> and stamped as <see cref="TelemetryConstants.TagNames.HopPredecessor"/>,
/// so ordering and causality stay reconstructable even though the spans are siblings.
/// </para>
/// </summary>
public static class FlatLaneActivity
{
    /// <summary>
    /// Starts a span parented to the trace lane anchor, degrading to today's ambient/predecessor
    /// parent whenever the anchor is absent, unparseable, or belongs to another trace.
    /// </summary>
    /// <param name="source">The ActivitySource to start on. Must be registered in
    /// <c>Telemetry:Tracing:AdditionalSources</c> or the span is never exported.</param>
    /// <param name="name">Span name.</param>
    /// <param name="kind">Span kind. Always supplied by the caller, never forced here: job spans stay
    /// <see cref="ActivityKind.Consumer"/> so Elastic APM keeps classifying them as transactions
    /// (apm-server keys that off SpanKind), while in-process lane items use
    /// <see cref="ActivityKind.Internal"/> so transaction counts and service-map edges do not move.</param>
    /// <param name="anchorTraceParent">The lane anchor; when null, <see cref="WorkflowTraceLane.Current"/> is used.</param>
    /// <param name="predecessorTraceParent">The immediate logical predecessor, linked rather than parented.</param>
    /// <param name="traceState">W3C tracestate accompanying both contexts — anchor and predecessor are
    /// in the same trace by construction, so one tracestate covers both.</param>
    public static Activity? Start(
        ActivitySource source,
        string name,
        ActivityKind kind,
        string? anchorTraceParent,
        string? predecessorTraceParent,
        string? traceState)
    {
        var anchorValue = anchorTraceParent ?? WorkflowTraceLane.Current;

        // Today's parent, byte for byte: the predecessor if we were given one, else the ambient span.
        var hasPredecessor = TryParse(predecessorTraceParent, traceState, out var predecessorContext);
        var fallbackParent = hasPredecessor
            ? predecessorContext
            : Activity.Current?.Context ?? default;

        if (!TryParse(anchorValue, traceState, out var anchorContext))
        {
            // No usable anchor — behave exactly as before the flat-lane work existed.
            return StartFallback(source, name, kind, fallbackParent, predecessorContext, hasPredecessor, laned: false, mismatch: false);
        }

        // A lane anchor from another trace is never trusted as a parent: a stale AsyncLocal or a
        // relayed payload from an unrelated request would otherwise teleport this span into a
        // foreign trace. Same posture as ExecutionController's body-vs-transport trace check.
        //
        // Compared against the PREDECESSOR only, never against a bare ambient span. The ambient span
        // is routinely from another trace — a Dapr scheduler callback is its own trace by
        // construction — and treating that as a mismatch would reject every legitimate anchor on the
        // job path. A foreign ambient span is handled by demoting it to a link (see BuildLinks).
        if (hasPredecessor && predecessorContext.TraceId != anchorContext.TraceId)
        {
            var mismatched = StartFallback(
                source, name, kind, fallbackParent, predecessorContext, hasPredecessor, laned: false, mismatch: true);
            return mismatched;
        }

        var links = BuildLinks(anchorContext, predecessorContext, hasPredecessor, out var demotedAmbient);

        var activity = source.StartActivity(
            name,
            kind,
            parentContext: anchorContext,
            tags: null,
            links: links);

        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.TraceLane, true);
        activity.SetTag(TelemetryConstants.TagNames.TraceLaneAnchor, anchorContext.SpanId.ToString());
        if (hasPredecessor)
            activity.SetTag(TelemetryConstants.TagNames.HopPredecessor, predecessorContext.SpanId.ToString());
        if (demotedAmbient)
            activity.SetTag(TelemetryConstants.TagNames.DaprCallback, true);

        return activity;
    }

    private static Activity? StartFallback(
        ActivitySource source,
        string name,
        ActivityKind kind,
        ActivityContext fallbackParent,
        ActivityContext predecessorContext,
        bool hasPredecessor,
        bool laned,
        bool mismatch)
    {
        IEnumerable<ActivityLink>? links = null;
        var ambient = Activity.Current;
        if (ambient is not null && hasPredecessor && ambient.Context.TraceId != predecessorContext.TraceId)
        {
            // Preserves the pre-existing "link the Dapr callback we did not parent to" behaviour.
            links = [new ActivityLink(ambient.Context)];
        }

        var activity = source.StartActivity(name, kind, fallbackParent, tags: null, links: links);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.TraceLane, laned);
        if (mismatch)
            activity.SetTag(TelemetryConstants.TagNames.TraceLaneMismatch, true);
        if (hasPredecessor)
            activity.SetTag(TelemetryConstants.TagNames.HopPredecessor, predecessorContext.SpanId.ToString());
        if (links is not null)
            activity.SetTag(TelemetryConstants.TagNames.DaprCallback, true);

        return activity;
    }

    private static IEnumerable<ActivityLink>? BuildLinks(
        ActivityContext anchorContext,
        ActivityContext predecessorContext,
        bool hasPredecessor,
        out bool demotedAmbient)
    {
        List<ActivityLink>? links = null;
        demotedAmbient = false;

        // hop N -> hop N+1: linked, not parented. Skipped when the predecessor IS the anchor,
        // which is the first hop of a lane (nothing to add beyond the parent edge).
        if (hasPredecessor && predecessorContext.SpanId != anchorContext.SpanId)
        {
            links = [new ActivityLink(predecessorContext)];
        }

        var ambient = Activity.Current;
        if (ambient is not null && ambient.Context.TraceId != anchorContext.TraceId)
        {
            links ??= [];
            links.Add(new ActivityLink(ambient.Context));
            demotedAmbient = true;
        }

        return links;
    }

    private static bool TryParse(string? traceParent, string? traceState, out ActivityContext context)
    {
        if (string.IsNullOrEmpty(traceParent))
        {
            context = default;
            return false;
        }

        // isRemote: true matches the pre-existing StartActivityContinuingTrace policy. It does not
        // change the emitted span shape (OTLP carries no parent-is-remote flag); the anchor is an
        // explicit context either way, which is what severs Activity.Parent.
        return ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
    }
}
