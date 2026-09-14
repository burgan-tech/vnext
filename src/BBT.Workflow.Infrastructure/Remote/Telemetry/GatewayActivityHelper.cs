using System.Diagnostics;
using BBT.Workflow.Discovery;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Remote;

/// <summary>
/// Spans for the cross-domain transport layer — the one outbound hop every <c>Remote*</c> service
/// makes, whichever wire it takes.
/// <para>
/// Before this, a remote call was visible only as whatever client instrumentation happened to draw:
/// an HttpClient span with no vNext context, and on the Dapr leg the retry policy, a circuit-breaker
/// open and the sidecar's <c>ERR_DIRECT_INVOKE</c> normalization were invisible entirely — the
/// failures that matter most on a cross-domain call were the ones the trace could not show.
/// </para>
/// </summary>
public static class GatewayActivityHelper
{
    /// <summary>
    /// Source name as a compile-time constant.
    /// <para>
    /// Never identify this source through <c>ActivitySource.Name</c> — reading the field runs this
    /// class's static initializer, and <c>AddActivityListener</c> invokes every registered predicate
    /// while constructing sources, so a predicate touching the field re-enters an initializer that
    /// is still running and poisons the type for the process. A const is inlined and triggers nothing.
    /// </para>
    /// <para>
    /// Its own source rather than <c>BBT.Workflow.Pipeline</c>: this family carries both command and
    /// query traffic across a network boundary, so folding it into the pipeline's source would skew
    /// every pipeline duration aggregation built on that name. It also buys a config-level kill
    /// switch on the one family whose volume cannot be bounded from code — removing it from
    /// <c>AdditionalSources</c> makes <c>StartActivity</c> return null and the span is never created.
    /// </para>
    /// </summary>
    public const string SourceName = TelemetryConstants.ActivitySources.Gateway;

    /// <summary>ActivitySource for cross-domain transport spans. Registered in all four hosts.</summary>
    public static readonly ActivitySource ActivitySource = new(SourceName);

    /// <summary>
    /// Starts the span covering one outbound cross-domain call, including every retry the policy
    /// makes — one span per logical call, never one per attempt: per-attempt spans multiply export
    /// volume exactly during the outage they are meant to describe, and the client instrumentation
    /// already draws each attempt.
    /// </summary>
    /// <param name="clientName">Typed client name — bounded cardinality, so it belongs in the span name.</param>
    /// <param name="transport">
    /// <c>dapr</c> or <c>http</c>. Read from the endpoint kind the discovery provider decided, never
    /// re-derived, so the tag cannot disagree with the wire actually used.
    /// </param>
    /// <param name="endpoint">The resolved endpoint; only its bounded identity is tagged.</param>
    public static Activity? StartSend(string clientName, string transport, DiscoveryEndpoint endpoint)
    {
        // IMPLICIT parent. The explicit-context overload sets ParentSpanId but leaves
        // Activity.Parent null, and baggage is inherited through the Activity chain — an explicitly
        // parented span here would sever the root-instance baggage that the outbound header helper
        // reads back out one frame below.
        var activity = ActivitySource.StartActivity($"Remote.Send/{clientName}", ActivityKind.Client);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.RemoteTransport, transport);

        // The relative path is deliberately NOT tagged and never in the name: it carries instance
        // ids, which would make the span name unbounded and the tag a per-instance series.
        activity.SetTag(TelemetryConstants.TagNames.RemoteHost, endpoint.BaseUrl.Host);
        if (endpoint.DaprAppId is { Length: > 0 } appId)
            activity.SetTag(TelemetryConstants.TagNames.DaprAppId, appId);

        return activity;
    }
}
