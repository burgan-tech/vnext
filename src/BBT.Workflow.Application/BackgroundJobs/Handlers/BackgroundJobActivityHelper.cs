using System.Diagnostics;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Provides centralized tracing functionality for background job handlers.
/// This helper class reduces code duplication by providing common methods for
/// starting activities with trace context and enriching them with observability data.
/// </summary>
public static class BackgroundJobActivityHelper
{
    /// <summary>
    /// ActivitySource for creating activities correlated with the original trace context.
    /// Used by all background job handlers for distributed tracing correlation.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.BackgroundJobs");

    /// <summary>
    /// Continues the ORIGINAL trace captured at enqueue time: the payload's TraceParent becomes
    /// the real parent, so the job (and everything it spawns — pipeline, Execution invoke, remote
    /// tasks) appears inside the caller's trace tree in APM. The ambient Dapr scheduler-callback
    /// span (POST /job/{name}) is retained as searchable id tags rather than an ActivityLink, so
    /// Elastic does not splice the callback transport trace into the business waterfall. Use for
    /// jobs that fire immediately after enqueue (transition jobs, state notify); deferred jobs
    /// (timers, timeouts) must use <see cref="StartDeferredActivity"/> so hours-old traces are not
    /// resurrected.
    /// Falls back to the ambient parent when the payload carries no or invalid trace context.
    /// </summary>
    public static Activity? StartActivityContinuingTrace(string activityName, ITraceableJobPayload payload)
    {
        if (!string.IsNullOrEmpty(payload.TraceParent) &&
            ActivityContext.TryParse(payload.TraceParent, payload.TraceState, isRemote: true, out var originalContext))
        {
            var ambient = Activity.Current;
            var callbackContext = ambient is not null && ambient.Context.TraceId != originalContext.TraceId
                ? ambient.Context
                : default;

            var activity = ActivitySource.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: originalContext,
                tags: null,
                links: null);
            StampDaprCallback(activity, callbackContext);

            return activity;
        }

        return ActivitySource.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext: Activity.Current?.Context ?? default);
    }

    /// <summary>
    /// Starts a new activity as a child of the current ambient span (e.g. the Dapr HTTP
    /// invocation span), so the full execution tree is visible under the caller's trace.
    /// If the payload carries a TraceParent from when the job was originally enqueued, that
    /// context is retained as searchable id tags for cross-trace correlation.
    /// This is the DEFERRED-job policy: timer/timeout/ack jobs fire long after enqueue, so the
    /// original trace is referenced by searchable id tags only; immediate jobs should use
    /// <see cref="StartActivityContinuingTrace"/> to stay inside the originating trace.
    /// </summary>
    public static Activity? StartDeferredActivity(string activityName, ITraceableJobPayload payload)
    {
        var originalContext = default(ActivityContext);
        var hasOrigin = !string.IsNullOrEmpty(payload.TraceParent) &&
            ActivityContext.TryParse(payload.TraceParent, payload.TraceState, out originalContext) &&
            originalContext.TraceId != Activity.Current?.Context.TraceId;

        var activity = ActivitySource.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext: Activity.Current?.Context ?? default,
            tags: null,
            links: null);
        if (activity is not null && hasOrigin)
        {
            activity.SetTag(TelemetryConstants.TagNames.OriginTraceId, originalContext.TraceId.ToString());
            activity.SetTag(TelemetryConstants.TagNames.OriginSpanId, originalContext.SpanId.ToString());
        }

        return activity;
    }

    /// <summary>
    /// Starts the job's span as a <em>flat-lane</em> item: parented to the payload's trace lane
    /// anchor so that every hop of the same instance is a SIBLING. The enqueue-time
    /// <c>TraceParent</c> is retained as the searchable <c>vnext.hop.predecessor</c> tag.
    /// <para>
    /// This is what removes the old "nesting depth == chain depth" behaviour. Kind stays
    /// <see cref="ActivityKind.Consumer"/> so Elastic APM keeps treating the job as a transaction.
    /// When the payload carries no anchor (older build, or a deliberately lane-free deferred job)
    /// this degrades to <see cref="StartActivityContinuingTrace"/>'s parenting exactly.
    /// </para>
    /// </summary>
    public static Activity? StartFlatLaneActivity(string activityName, ITraceableJobPayload payload)
        => FlatLaneActivity.Start(
            ActivitySource,
            activityName,
            ActivityKind.Consumer,
            anchorTraceParent: payload.TraceRoot,
            predecessorTraceParent: payload.TraceParent,
            traceState: payload.TraceState);

    /// <summary>
    /// Names the Dapr scheduler round-trip that arms an already-persisted job
    /// (<c>IBackgroundJobArmHandle.ArmAsync</c>). Aether's own <c>BackgroundJob.Schedule*</c> spans
    /// are Verbose-gated, so in Business mode the arm — the dominant term of the async accept's
    /// tail, and the start of the dead time before the job span begins — was invisible. Implicit
    /// parent, Business category, like every other in-process span.
    /// </summary>
    public static Activity? StartArmActivity(string jobName)
    {
        var activity = ActivitySource.StartActivity("BackgroundJob.Arm", ActivityKind.Internal);
        if (activity is null) return null;

        activity.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);
        activity.SetTag(TelemetryConstants.TagNames.JobName, jobName);
        return activity;
    }

    private static void StampDaprCallback(Activity? activity, ActivityContext callbackContext)
    {
        if (activity is null || callbackContext == default) return;

        activity.SetTag(TelemetryConstants.TagNames.DaprCallback, true);
        activity.SetTag(TelemetryConstants.TagNames.DaprCallbackTraceId, callbackContext.TraceId.ToString());
        activity.SetTag(TelemetryConstants.TagNames.DaprCallbackSpanId, callbackContext.SpanId.ToString());
    }

    /// <summary>
    /// Enriches the activity with common job-specific tags for observability.
    /// </summary>
    /// <param name="activity">The activity to enrich.</param>
    /// <param name="payload">The job payload containing observability data.</param>
    public static void EnrichActivity(Activity? activity, ITraceableJobPayload payload)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, payload.Domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, payload.FlowName);
        activity.SetTag(TelemetryConstants.TagNames.FlowVersion, payload.Version);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, payload.InstanceId);
        activity.SetTag(TelemetryConstants.TagNames.JobName, payload.JobName);
        activity.SetTag("messaging.system", "dapr");
        activity.SetTag("messaging.operation", "process");

        // Lane ordering/searchability. Siblings are sorted visually by @timestamp, so these exist
        // for querying and for reconstructing a lane programmatically. LaneSeq is the reliable
        // ordinal: ChainDepth resets to 0 at every resume/timeout/retry boundary.
        if (payload is TransitionJobPayload transitionPayload)
        {
            activity.SetTag(TelemetryConstants.TagNames.ChainDepth, transitionPayload.ChainDepth);
            activity.SetTag(TelemetryConstants.TagNames.LaneSeq, transitionPayload.LaneSeq);
        }
    }

    /// <summary>
    /// Enriches the activity with additional transition-specific tags.
    /// </summary>
    /// <param name="activity">The activity to enrich.</param>
    /// <param name="transitionKey">The transition key to add as a tag.</param>
    public static void EnrichActivityWithTransition(Activity? activity, string transitionKey)
    {
        activity?.SetTag(TelemetryConstants.TagNames.TransitionKey, transitionKey);
    }
}
