using System.Diagnostics;
using BBT.Workflow.Logging;

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
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.SubFlow");

    /// <summary>
    /// Starts a new activity as a child of the current activity.
    /// If Activity.Current exists, the new activity will be linked as a child span.
    /// </summary>
    /// <param name="operationName">The name of the operation.</param>
    /// <param name="kind">The kind of activity (default: Internal).</param>
    /// <returns>A new Activity linked to the current trace context, or null if no listener.</returns>
    public static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        // Get the current activity's context to establish parent-child relationship
        var parentContext = Activity.Current?.Context ?? default;
        
        return ActivitySource.StartActivity(
            operationName,
            kind,
            parentContext);
    }

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
        activity.SetTag("vnext.subflow.instance.id", subInstanceId);
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
    public static void EnrichWithStart(
        Activity? activity,
        Guid parentInstanceId,
        string subFlowDomain,
        string subFlowKey,
        Guid subFlowInstanceId)
    {
        if (activity is null) return;

        activity.SetTag(TelemetryConstants.TagNames.InstanceId, parentInstanceId);
        activity.SetTag("vnext.subflow.domain", subFlowDomain);
        activity.SetTag("vnext.subflow.flow", subFlowKey);
        activity.SetTag("vnext.subflow.instance.id", subFlowInstanceId);
        activity.SetTag("vnext.subflow.operation", "start");
    }

    /// <summary>
    /// Enriches the activity with SubFlow forward context.
    /// </summary>
    /// <param name="activity">The activity to enrich.</param>
    /// <param name="subFlowInstanceId">The SubFlow instance ID being forwarded to.</param>
    /// <param name="transitionKey">The transition key being forwarded.</param>
    public static void EnrichWithForward(
        Activity? activity,
        Guid subFlowInstanceId,
        string transitionKey)
    {
        if (activity is null) return;

        activity.SetTag("vnext.subflow.instance.id", subFlowInstanceId);
        activity.SetTag(TelemetryConstants.TagNames.TransitionKey, transitionKey);
        activity.SetTag("vnext.subflow.operation", "forward");
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

