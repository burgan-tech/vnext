using System.Diagnostics;

namespace BBT.Workflow.Logging;

/// <summary>
/// Stamps W3C trace context into Dapr metadata dictionaries.
/// <para>
/// Dapr output bindings and pub/sub publishes do not inherit the ambient trace the way an outbound
/// <c>HttpClient</c> call does — the sidecar originates its own request to the component, so
/// <c>DiagnosticsHandler</c> never sees the hop and no <c>traceparent</c> reaches the far side.
/// Passing it as component metadata is what keeps the remote leg attached to the current trace.
/// </para>
/// <para>
/// Always stamped from the <b>live</b> <see cref="Activity.Current"/>. A traceparent copied out of a
/// task definition or a job payload would point at a span that has already ended and would detach
/// the downstream component from the trace that is actually running.
/// </para>
/// </summary>
public static class DaprTraceMetadata
{
    private const string TraceParentKey = "traceparent";
    private const string TraceStateKey = "tracestate";

    /// <summary>CloudEvents envelope attribute names used by Dapr pub/sub.</summary>
    private const string CloudEventTraceParentKey = "cloudevent.traceparent";
    private const string CloudEventTraceStateKey = "cloudevent.tracestate";

    /// <summary>
    /// Stamps <c>traceparent</c>/<c>tracestate</c> for an output binding, overwriting any existing
    /// values — a stale traceparent is worse than none, because it silently reparents the remote
    /// span onto a finished trace.
    /// </summary>
    public static void StampBinding(IDictionary<string, string> metadata)
    {
        var activity = Activity.Current;
        if (activity?.Id is null) return;

        metadata[TraceParentKey] = activity.Id;
        if (!string.IsNullOrEmpty(activity.TraceStateString))
            metadata[TraceStateKey] = activity.TraceStateString;
    }

    /// <summary>
    /// Stamps the CloudEvents trace attributes for a pub/sub publish.
    /// </summary>
    public static void StampCloudEvent(IDictionary<string, string> metadata)
    {
        var activity = Activity.Current;
        if (activity?.Id is null) return;

        metadata[CloudEventTraceParentKey] = activity.Id;
        if (!string.IsNullOrEmpty(activity.TraceStateString))
            metadata[CloudEventTraceStateKey] = activity.TraceStateString;
    }
}
