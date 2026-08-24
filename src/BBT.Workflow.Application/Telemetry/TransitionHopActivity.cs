using System.Diagnostics;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Starts the span for one hop of an <b>in-process</b> transition chain, so an inline auto-chain
/// produces the same trace shape a per-hop scheduler job produced.
/// <para>
/// With <c>AutoTransitionMode.Scheduled</c> every chained hop was its own background job, and
/// <c>BackgroundJobActivityHelper.StartFlatLaneActivity</c> gave it a flat-lane span: parented to
/// the lane anchor, the predecessor hop attached as an <see cref="ActivityLink"/>, ordered by
/// <c>LaneSeq</c>. Inline mode removes the job but must not remove the span — without one, a whole
/// chain collapses into the single job span and the per-hop timing, ordering and causality that
/// dashboards and traces are built on disappear.
/// </para>
/// <para>
/// Parenting policy is NOT re-implemented here: it delegates to <see cref="FlatLaneActivity.Start"/>,
/// the single home of that policy, exactly as the job path does.
/// </para>
/// </summary>
public static class TransitionHopActivity
{
    /// <summary>
    /// Name PREFIX for an inline chain hop; the full name adds <c>/{domain}/{flow}/{transition}</c>
    /// via <see cref="TransitionSpanName.Build"/>. Deliberately distinct from the job path's
    /// <c>TransitionJob.Execute</c>: there is no job here, and sharing the prefix would make
    /// "how many transition jobs ran" unanswerable from traces.
    /// </summary>
    public const string ActivityName = TransitionSpanName.HopPrefix;

    /// <summary>
    /// Starts the hop span.
    /// <para>
    /// Kind is <see cref="ActivityKind.Consumer"/>, matching the job path rather than the
    /// <see cref="ActivityKind.Internal"/> that in-process lane items normally use. That is a
    /// deliberate parity choice: apm-server classifies transactions by SpanKind, so an Internal
    /// hop would silently drop every chained transition out of transaction counts and alerts that
    /// were built while those hops were jobs.
    /// </para>
    /// </summary>
    /// <param name="context">The hop being executed; supplies the identity tags.</param>
    /// <param name="laneSeq">
    /// This hop's ordinal within the lane, already advanced by the caller. Passed in rather than
    /// read from the ambient lane so the tag can never disagree with the lane scope the caller
    /// opened for the hop.
    /// </param>
    /// <param name="predecessorTraceParent">
    /// The span of the PREVIOUS hop (W3C traceparent), captured while it was still current. It is
    /// linked, never parented — that split is what makes hop N+1 a sibling of hop N instead of its
    /// child, and it is why the caller cannot simply read <see cref="Activity.Current"/> here: by
    /// the time the next hop starts, the previous hop's span has already been disposed and
    /// <c>Activity.Current</c> has fallen back to the enclosing job span.
    /// </param>
    /// <param name="predecessorTraceState">W3C tracestate accompanying the predecessor.</param>
    public static Activity? Start(
        TransitionExecutionContext context,
        int laneSeq,
        string? predecessorTraceParent,
        string? predecessorTraceState)
    {
        // Anchor is left null so FlatLaneActivity reads the ambient lane — the inline hop runs
        // inside the lane its caller established, unlike a job which carries the anchor in its
        // payload across the scheduler.
        var activity = FlatLaneActivity.Start(
            PipelineStepActivityHelper.ActivitySource,
            TransitionSpanName.Build(
                ActivityName, context.Domain, context.WorkflowKey, context.TransitionKey),
            ActivityKind.Consumer,
            anchorTraceParent: null,
            predecessorTraceParent: predecessorTraceParent,
            traceState: predecessorTraceState);

        if (activity is null) return null;

        // Same tag set the job path stamps (BackgroundJobActivityHelper.EnrichActivity plus the
        // transition key), minus the messaging.* pair and the job name: there is no broker and no
        // job behind an inline hop, and claiming otherwise would corrupt messaging dashboards.
        activity.SetTag(TelemetryConstants.TagNames.Domain, context.Domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, context.WorkflowKey);
        activity.SetTag(TelemetryConstants.TagNames.FlowVersion, context.Workflow.Version);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, context.InstanceId);
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, context.TransitionKey);
        activity.SetTag(TelemetryConstants.TagNames.ChainDepth, context.ChainDepth);
        activity.SetTag(TelemetryConstants.TagNames.LaneSeq, laneSeq);

        return activity;
    }
}
