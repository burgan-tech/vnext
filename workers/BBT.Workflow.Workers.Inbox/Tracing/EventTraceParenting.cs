using System.Collections.Generic;
using System.Diagnostics;

namespace BBT.Workflow.Workers.Inbox.Tracing;

/// <summary>
/// Pure parenting-decision logic extracted from <see cref="EventTraceScope"/> so the mode split can
/// be pinned by a fast unit test instead of relying solely on Faz C's OpenObserve acceptance checks
/// (the Inbox worker has no dedicated test project). Deliberately has NO access to
/// <see cref="Activity.Current"/> or <see cref="ActivitySource"/> — those side effects (clearing/
/// restoring the ambient activity to force a true root, actually starting the span) stay in
/// <see cref="EventTraceScope.Start"/>, which is the only caller.
/// </summary>
internal static class EventTraceParenting
{
    /// <summary>
    /// Resolves the parent context and links a consumer span should be started with.
    /// </summary>
    /// <param name="mode">
    /// <see cref="EventTraceMode.ContinueTrace"/> parents the new span onto the event's own trace
    /// (falling back to the ambient context, then to a genuine root, when the event carries none).
    /// <see cref="EventTraceMode.LinkedDelivery"/> always roots a new trace and links the producer's
    /// context (and the ambient one, if present) instead of parenting onto either.
    /// </param>
    /// <param name="traceParent">The event's W3C traceparent, or null/empty when it carries none.</param>
    /// <param name="traceState">The event's W3C tracestate accompanying <paramref name="traceParent"/> — rides the link, not just the traceparent.</param>
    /// <param name="ambient">
    /// The context of whatever <see cref="Activity"/> was ambient before this call (e.g. the pub/sub
    /// delivery span), or <c>default</c> when there was none. The caller captures this BEFORE
    /// clearing <see cref="Activity.Current"/> for <see cref="EventTraceMode.LinkedDelivery"/> — this
    /// method never reads ambient state itself, which is what keeps it unit-testable.
    /// </param>
    /// <returns>
    /// The parent context to start the new span with, and the links to attach. For
    /// <see cref="EventTraceMode.LinkedDelivery"/> the returned parent is always <c>default</c> — the
    /// caller must ALSO clear <see cref="Activity.Current"/> before calling
    /// <c>ActivitySource.StartActivity</c>, or the .NET tracing API silently falls back to the
    /// ambient activity instead of creating a true root (a default <see cref="ActivityContext"/>
    /// parent alone does not force one).
    /// </returns>
    public static (ActivityContext ParentContext, IEnumerable<ActivityLink>? Links) ResolveParenting(
        EventTraceMode mode,
        string? traceParent,
        string? traceState,
        ActivityContext ambient)
    {
        var hasAmbient = ambient != default;
        var hasOrigin = !string.IsNullOrEmpty(traceParent) &&
            ActivityContext.TryParse(traceParent, traceState, isRemote: true, out var originContext);

        if (mode == EventTraceMode.ContinueTrace)
        {
            if (hasOrigin)
            {
                IEnumerable<ActivityLink>? links = hasAmbient && ambient.TraceId != originContext.TraceId
                    ? new[] { new ActivityLink(ambient) }
                    : null;
                return (originContext, links);
            }

            return (hasAmbient ? ambient : default, null);
        }

        // LinkedDelivery: the handler roots its own trace; producer + ambient become links only.
        List<ActivityLink>? links2 = null;
        if (hasOrigin)
        {
            (links2 ??= new List<ActivityLink>()).Add(new ActivityLink(originContext));
        }

        if (hasAmbient)
        {
            (links2 ??= new List<ActivityLink>()).Add(new ActivityLink(ambient));
        }

        return (default, links2);
    }
}
