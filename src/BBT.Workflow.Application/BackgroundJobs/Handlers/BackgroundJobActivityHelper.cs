using System.Diagnostics;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Logging;

namespace BBT.Workflow.BackgroundJobs.Handlers;

/// <summary>
/// Provides centralized tracing functionality for background job handlers.
/// This helper class reduces code duplication by providing common methods for
/// starting activities with trace context and enriching them with observability data.
/// </summary>
public static class BackgroundJobActivityHelper
{
    /// <summary>
    /// ActivitySource for creating activities linked to the original trace context.
    /// Used by all background job handlers for distributed tracing correlation.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.BackgroundJobs");

    /// <summary>
    /// Continues the ORIGINAL trace captured at enqueue time: the payload's TraceParent becomes
    /// the real parent, so the job (and everything it spawns — pipeline, Execution invoke, remote
    /// tasks) appears inside the caller's trace tree in APM. The ambient Dapr scheduler-callback
    /// span (POST /job/{name}) is attached as an ActivityLink instead. Use for jobs that fire
    /// immediately after enqueue (transition jobs, state notify); deferred jobs (timers, timeouts)
    /// must use <see cref="StartActivityAsChildWithLink"/> so hours-old traces are not resurrected.
    /// Falls back to the ambient parent when the payload carries no or invalid trace context.
    /// </summary>
    public static Activity? StartActivityContinuingTrace(string activityName, ITraceableJobPayload payload)
    {
        if (!string.IsNullOrEmpty(payload.TraceParent) &&
            ActivityContext.TryParse(payload.TraceParent, payload.TraceState, isRemote: true, out var originalContext))
        {
            IEnumerable<ActivityLink>? links = null;
            var ambient = Activity.Current;
            if (ambient is not null && ambient.Context.TraceId != originalContext.TraceId)
            {
                links = [new ActivityLink(ambient.Context)];
            }

            var activity = ActivitySource.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: originalContext,
                tags: null,
                links: links);
            if (links is not null)
            {
                activity?.SetTag("vnext.dapr.callback", true);
            }

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
    /// If the payload carries a TraceParent from when the job was originally enqueued,
    /// that context is attached as an ActivityLink for cross-trace correlation.
    /// This is the DEFERRED-job policy: timer/timeout/ack jobs fire long after enqueue, so the
    /// original trace is referenced by link only; immediate jobs should use
    /// <see cref="StartActivityContinuingTrace"/> to stay inside the originating trace.
    /// </summary>
    public static Activity? StartActivityAsChildWithLink(string activityName, ITraceableJobPayload payload)
    {
        IEnumerable<ActivityLink>? links = null;

        if (!string.IsNullOrEmpty(payload.TraceParent) &&
            ActivityContext.TryParse(payload.TraceParent, payload.TraceState, out var originalContext) &&
            originalContext.TraceId != Activity.Current?.Context.TraceId)
        {
            links = [new ActivityLink(originalContext)];
        }

        return ActivitySource.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext: Activity.Current?.Context ?? default,
            tags: null,
            links: links);
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

