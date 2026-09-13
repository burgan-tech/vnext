using System.Diagnostics;
using BBT.Workflow.Logging;
using BBT.Workflow.Telemetry;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Provides centralized tracing functionality for SubFlow services.
/// This helper class creates child spans from the current activity for distributed tracing.
/// </summary>
public static class SubFlowActivityHelper
{
    /// <summary>
    /// ActivitySource for creating SubFlow-related activities.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(TelemetryConstants.ActivitySources.SubFlow);

    /// <summary>
    /// Starts a new activity as a child of the current activity.
    /// If Activity.Current exists, the new activity will be linked as a child span.
    /// </summary>
    /// <param name="operationName">The name of the operation.</param>
    /// <param name="kind">The kind of activity (default: Internal).</param>
    /// <returns>A new Activity linked to the current trace context, or null if no listener.</returns>
    public static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(
            operationName,
            kind);
    }

    /// <summary>
    /// Starts a <em>flat-lane</em> SubFlow span: parented to the supplied lane anchor rather than to
    /// whatever span happens to be ambient.
    /// <para>
    /// Used for the parent resume. A resume is a <b>parent-instance</b> operation — it runs after the
    /// completion commits, in its own scope and UoW — so it belongs in the parent's lane
    /// (<c>WorkflowTraceLane.ParentLane</c>), not nested inside the subflow's completion span where
    /// the old nesting used to pile up.
    /// </para>
    /// </summary>
    public static Activity? StartFlatLaneActivity(
        string operationName,
        string? anchorTraceParent,
        ActivityKind kind = ActivityKind.Internal)
        => FlatLaneActivity.Start(
            ActivitySource,
            operationName,
            kind,
            anchorTraceParent: anchorTraceParent,
            predecessorTraceParent: Activity.Current?.Id,
            traceState: Activity.Current?.TraceStateString);

    /// <summary>
    /// Enriches the activity with SubFlow completion context.
    /// </summary>
    /// <param name="activity">The activity to enrich.</param>
    /// <param name="subInstanceId">The SubFlow instance ID.</param>
    /// <param name="parentInstanceId">The parent instance ID.</param>
    /// <param name="domain">The domain name.</param>
    /// <param name="flow">The workflow name.</param>
    public static void EnrichWithCompletion(
        Activity? activity,
        Guid subInstanceId,
        Guid parentInstanceId,
        string domain,
        string flow)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstanceId);
        activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subInstanceId);
        activity.SetTag("vnext.subflow.operation", "completion");
    }

    /// <summary>
    /// Enriches the activity with SubFlow start context.
    /// </summary>
    /// <param name="activity">The activity to enrich.</param>
    /// <param name="parentInstanceId">The parent instance ID.</param>
    /// <param name="subFlowDomain">The SubFlow domain.</param>
    /// <param name="subFlowKey">The SubFlow workflow key.</param>
    /// <param name="subFlowInstanceId">The SubFlow instance ID.</param>
    /// <param name="rootInstanceId">Optional root instance ID for nested subflow chains.</param>
    public static void EnrichWithStart(
        Activity? activity,
        Guid parentInstanceId,
        string subFlowDomain,
        string subFlowKey,
        Guid subFlowInstanceId,
        Guid rootInstanceId = default)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstanceId);
        activity.SetTag("vnext.subflow.domain", subFlowDomain);
        activity.SetTag("vnext.subflow.flow", subFlowKey);
        activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subFlowInstanceId);
        if (rootInstanceId != default)
        {
            activity.SetTag(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId.ToString());
            activity.SetBaggage(TelemetryConstants.TagNames.RootInstanceId, rootInstanceId.ToString());
        }
        activity.SetTag("vnext.subflow.operation", "start");
    }

    /// <summary>
    /// Enriches the activity with SubFlow forward context.
    /// </summary>
    /// <param name="activity">The activity to enrich.</param>
    /// <param name="subFlowInstanceId">The SubFlow instance ID being forwarded to.</param>
    /// <param name="transitionKey">The transition key being forwarded.</param>
    /// <param name="parentInstanceId">Optional parent instance ID for trace/log correlation when forwarding cross-domain.</param>
    public static void EnrichWithForward(
        Activity? activity,
        Guid subFlowInstanceId,
        string transitionKey,
        Guid? parentInstanceId = null)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subFlowInstanceId);
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, transitionKey);
        activity.SetTag("vnext.subflow.operation", "forward");
        if (parentInstanceId.HasValue)
        {
            activity.SetTag(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId.Value.ToString());
            activity.SetBaggage(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId.Value.ToString());
        }
    }

    /// <summary>
    /// Enriches the activity with SubFlow state change context.
    /// </summary>
    public static void EnrichWithStateChange(
        Activity? activity,
        Guid subInstanceId,
        Guid parentInstanceId,
        string domain,
        string flow,
        string newState)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        activity.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstanceId);
        activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, subInstanceId);
        activity.SetTag("vnext.subflow.operation", "state_change");
        activity.SetTag("vnext.subflow.new_state", newState);
    }

    /// <summary>
    /// Enriches the activity with child SubFlow cancellation context.
    /// </summary>
    public static void EnrichWithCancellation(
        Activity? activity,
        Guid instanceId,
        string domain,
        string flow)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, instanceId);
        activity.SetTag("vnext.subflow.operation", "cancellation");
    }

    /// <summary>
    /// Enriches the activity with child SubFlow fault context.
    /// <para>
    /// The twin of <see cref="EnrichWithCancellation"/>. A parent faulting cascades downward to
    /// every active SubFlow child, and that leg had no span at all while its cancel twin did — so a
    /// fault cascade was the one child-termination path a trace could not show.
    /// </para>
    /// </summary>
    public static void EnrichWithChildFault(
        Activity? activity,
        Guid instanceId,
        Guid parentInstanceId,
        string domain,
        string flow)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.Domain, domain);
        activity.SetTag(TelemetryConstants.TagNames.Flow, flow);
        activity.SetTag(TelemetryConstants.TagNames.SubflowInstanceId, instanceId);
        activity.SetTag(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId);
        activity.SetTag("vnext.subflow.operation", "child_fault");
    }

    /// <summary>
    /// Records what a SubFlow operation actually did, under <c>vnext.subflow.result</c>.
    /// <para>
    /// The early returns on these paths are "nothing happened" answers — the child was gone, or it
    /// was already terminal — and without this they are indistinguishable from a completed
    /// operation: same span name, same duration, no error. Same tag key the sub-state path uses, so
    /// one query covers both.
    /// </para>
    /// </summary>
    public static void SetOutcome(Activity? activity, string outcome)
    {
        activity?.SetTag("vnext.subflow.result", outcome);
    }

    /// <summary>
    /// Sets the activity status to OK.
    /// </summary>
    /// <param name="activity">The activity to update.</param>
    public static void SetSuccess(Activity? activity)
    {
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    /// <summary>
    /// Sets the activity status to Error with optional description.
    /// </summary>
    /// <param name="activity">The activity to update.</param>
    /// <param name="description">The error description.</param>
    /// <param name="exception">Optional exception to record.</param>
    public static void SetError(Activity? activity, string? description = null, Exception? exception = null)
    {
        if (activity is null) return;

        activity.SetStatus(ActivityStatusCode.Error, description);
        
        if (exception != null)
        {
            activity.AddException(exception);
            activity.SetTag("error.type", exception.GetType().Name);
        }
    }
}

