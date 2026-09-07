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
    /// Resolves the parent context a consumer span should be started with.
    /// </summary>
    /// <param name="mode">
    /// <see cref="EventTraceMode.ContinueTrace"/> parents the new span onto the event's own trace
    /// (falling back to the ambient context, then to a genuine root, when the event carries none).
    /// <see cref="EventTraceMode.IsolatedDelivery"/> always roots a new trace without linking the
    /// producer or ambient transport trace. Their ids are stamped by <see cref="EventTraceScope"/>
    /// as searchable tags instead.
    /// </param>
    /// <param name="traceParent">The event's W3C traceparent, or null/empty when it carries none.</param>
    /// <param name="traceState">The event's W3C tracestate accompanying <paramref name="traceParent"/>.</param>
    /// <param name="ambient">
    /// The context of whatever <see cref="Activity"/> was ambient before this call (e.g. the pub/sub
    /// delivery span), or <c>default</c> when there was none. The caller captures this BEFORE
    /// clearing <see cref="Activity.Current"/> for <see cref="EventTraceMode.IsolatedDelivery"/> — this
    /// method never reads ambient state itself, which is what keeps it unit-testable.
    /// </param>
    /// <returns>
    /// The parent context to start the new span with. For
    /// <see cref="EventTraceMode.IsolatedDelivery"/> it is always <c>default</c> — the
    /// caller must ALSO clear <see cref="Activity.Current"/> before calling
    /// <c>ActivitySource.StartActivity</c>, or the .NET tracing API silently falls back to the
    /// ambient activity instead of creating a true root (a default <see cref="ActivityContext"/>
    /// parent alone does not force one).
    /// </returns>
    public static ActivityContext ResolveParent(
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
                return originContext;
            }

            return hasAmbient ? ambient : default;
        }

        // IsolatedDelivery: cross-trace ActivityLinks make Elastic splice delayed fact/backup
        // delivery into the business waterfall. Correlation is retained as indexed id tags by the
        // scope instead, so this trace is genuinely isolated in both storage and presentation.
        return default;
    }
}
