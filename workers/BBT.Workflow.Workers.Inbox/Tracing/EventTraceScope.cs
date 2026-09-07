using System;
using System.Collections.Generic;
using System.Diagnostics;
using BBT.Aether.Tracing;
using BBT.Workflow.Events;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Workers.Inbox.Tracing;

/// <summary>
/// Starts the handler span for one consumed event and scopes the originating request id for the
/// duration of the handler so outbound forwards can propagate it.
/// <para>
/// <see cref="EventTraceMode.ContinueTrace"/> (immediate async COMMANDS) restores the publisher's
/// trace context exactly as before this split: the event's TraceParent becomes the real parent of
/// the handler span. A foreign ambient pub/sub delivery trace is not linked into the business
/// waterfall.
/// </para>
/// <para>
/// <see cref="EventTraceMode.IsolatedDelivery"/> (FACT deliveries) roots a brand-new trace for the
/// handler span without cross-trace ActivityLinks. Producer and ambient pub/sub ids are retained as
/// indexed tags, so Elastic cannot splice delayed backup delivery into the business waterfall.
/// Forcing a genuine root requires clearing <see cref="Activity.Current"/> around
/// the <c>StartActivity</c> call — a default <see cref="ActivityContext"/> parent alone does not do
/// it, .NET falls back to the ambient activity — so this scope restores the ambient in
/// <see cref="Dispose"/> once the handler body (which must see the new root as
/// <see cref="Activity.Current"/>) has run.
/// </para>
/// Lane <see cref="WorkflowTraceLane.Reset(string, string, int, ActivationEpisode)"/> and the
/// RequestId restore are
/// IDENTICAL in both modes — this is what keeps a genuine backup-settled subflow resume anchored
/// into the PARENT's trace regardless of which mode delivered it.
/// Dispose in reverse order of acquisition (activity first, correlation restore last).
/// </summary>
internal sealed class EventTraceScope : IDisposable
{
    /// <summary>
    /// ActivitySource for inbox event-consumption spans. Matched by the
    /// "BBT.Workflow.Workers.*" entry in the worker's Telemetry:Tracing:AdditionalSources.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(TelemetryConstants.ActivitySources.WorkersInbox);

    private readonly Activity? _activity;
    private readonly IDisposable? _correlationChange;
    private readonly IDisposable? _laneScope;
    private readonly Activity? _ambientToRestore;
    private readonly bool _restoreAmbient;

    private EventTraceScope(
        Activity? activity,
        IDisposable? correlationChange,
        IDisposable? laneScope,
        Activity? ambientToRestore,
        bool restoreAmbient)
    {
        _activity = activity;
        _correlationChange = correlationChange;
        _laneScope = laneScope;
        _ambientToRestore = ambientToRestore;
        _restoreAmbient = restoreAmbient;
    }

    /// <summary>
    /// Starts a consumer span for <paramref name="evt"/> under the given <paramref name="mode"/> and
    /// scopes the correlation id. <paramref name="mode"/> has NO default — every call site must state
    /// its classification explicitly (see <see cref="EventTraceMode"/>).
    /// </summary>
    /// <param name="activityName">Span name, e.g. "InstanceSubCompleted.Handle".</param>
    /// <param name="evt">The traceable event carrying TraceParent/TraceState/RequestId.</param>
    /// <param name="correlationIdProvider">Optional provider to scope the event's request id into.</param>
    /// <param name="mode">Whether the handler continues the producer's trace or roots its own.</param>
    /// <param name="messageId">
    /// The CloudEvent envelope id (<c>envelope.Id</c>). Stamped as both <c>messaging.message.id</c>
    /// and <c>vnext.causation.id</c> on a <see cref="EventTraceMode.IsolatedDelivery"/> root, which is
    /// now a trace entry point and needs to be findable by the message that produced it. Ignored for
    /// <see cref="EventTraceMode.ContinueTrace"/>. ContinueTrace only adds callback identity tags
    /// when the ambient delivery belongs to another trace.
    /// </param>
    /// <param name="deliveryAttempt">
    /// The event's rearm/redelivery attempt count, when it carries one (e.g. <c>RearmAttempt</c> on
    /// the subflow-terminal events). Stamped as <c>vnext.delivery.attempt</c> on an IsolatedDelivery
    /// root when provided; omitted entirely when null. Ignored for ContinueTrace.
    /// </param>
    public static EventTraceScope Start(
        string activityName,
        ITraceableDistributedEvent evt,
        ICorrelationIdProvider? correlationIdProvider,
        EventTraceMode mode,
        string? messageId = null,
        int? deliveryAttempt = null)
    {
        var previousAmbient = Activity.Current;
        var ambientContext = previousAmbient?.Context ?? default;

        var parentContext = EventTraceParenting.ResolveParent(
            mode, evt.TraceParent, evt.TraceState, ambientContext);

        var forcedRoot = mode == EventTraceMode.IsolatedDelivery;
        Activity? activity;

        if (forcedRoot)
        {
            // A default parentContext alone does NOT create a root span — ActivitySource.StartActivity
            // falls back to the ambient Activity.Current when one is set. Clearing it here is the only
            // way to force a genuine new trace; restored in Dispose once the handler body (which must
            // observe the new root as Activity.Current) has run. Exception-safe: if StartActivity
            // itself throws, restore immediately since no scope will exist to do it later.
            Activity.Current = null;
            try
            {
                activity = ActivitySource.StartActivity(
                    activityName,
                    ActivityKind.Consumer,
                    parentContext: parentContext,
                    tags: BuildDeliveryTags(evt, messageId, deliveryAttempt, ambientContext),
                    links: null);
            }
            catch
            {
                Activity.Current = previousAmbient;
                throw;
            }
        }
        else
        {
            activity = ActivitySource.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: parentContext,
                tags: null,
                links: null);
            StampDaprCallback(activity, ambientContext);
        }

        IDisposable? correlationChange = null;
        if (correlationIdProvider is not null && !string.IsNullOrEmpty(evt.RequestId))
        {
            correlationChange = correlationIdProvider.Change(evt.RequestId);
        }

        // Establish the trace lane for the handler body. Lane-aware events carry the publisher's
        // anchor; everything else anchors on this handler span. Without this, work started inside a
        // handler (a relayed transition, a republished event) would anchor on the pub/sub delivery
        // span and detach from the originating request's trace tree. IDENTICAL in both modes.
        var laneScope = evt is ILaneAwareDistributedEvent laneAware
            ? WorkflowTraceLane.Reset(
                laneAware.TraceRoot,
                laneAware.ParentTraceRoot,
                episode: ActivationEpisode.FromCarrier(
                    laneAware.EpisodeStartedAt, laneAware.EpisodeTrigger, laneAware.EpisodeTransitionKey,
                    laneAware.EpisodeTraceRoot))
            : WorkflowTraceLane.Reset(activity?.Id);

        return new EventTraceScope(
            activity,
            correlationChange,
            laneScope,
            forcedRoot ? previousAmbient : null,
            forcedRoot);
    }

    /// <summary>
    /// Identity and correlation tags for an IsolatedDelivery root: it is now a trace entry point,
    /// so it must be findable by the message that produced it and by the instance(s) it concerns.
    /// Property access is adapted per concrete event type since the shared
    /// <see cref="ITraceableDistributedEvent"/> contract does not expose Domain/Flow/instance ids
    /// (they differ in shape — e.g.
    /// <see cref="InstanceSubStateChangedEvent"/> has no bare "InstanceId", only
    /// Parent/SubInstanceId).
    /// </summary>
    private static IEnumerable<KeyValuePair<string, object?>> BuildDeliveryTags(
        ITraceableDistributedEvent evt,
        string? messageId,
        int? deliveryAttempt,
        ActivityContext ambientContext)
    {
        var tags = new List<KeyValuePair<string, object?>>();

        if (!string.IsNullOrEmpty(messageId))
        {
            tags.Add(new KeyValuePair<string, object?>(
                TelemetryConstants.TagNames.MessagingMessageId, messageId));
            tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.CausationId, messageId));
        }

        if (deliveryAttempt.HasValue)
        {
            tags.Add(new KeyValuePair<string, object?>(
                TelemetryConstants.TagNames.DeliveryAttempt, deliveryAttempt.Value));
        }

        if (!string.IsNullOrEmpty(evt.TraceParent) &&
            ActivityContext.TryParse(evt.TraceParent, evt.TraceState, isRemote: true, out var originContext))
        {
            tags.Add(new KeyValuePair<string, object?>(
                TelemetryConstants.TagNames.OriginTraceId, originContext.TraceId.ToString()));
            tags.Add(new KeyValuePair<string, object?>(
                TelemetryConstants.TagNames.OriginSpanId, originContext.SpanId.ToString()));
        }

        if (ambientContext != default)
        {
            tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.DaprCallback, true));
            tags.Add(new KeyValuePair<string, object?>(
                TelemetryConstants.TagNames.DaprCallbackTraceId, ambientContext.TraceId.ToString()));
            tags.Add(new KeyValuePair<string, object?>(
                TelemetryConstants.TagNames.DaprCallbackSpanId, ambientContext.SpanId.ToString()));
        }

        switch (evt)
        {
            case InstanceCanceledEvent e:
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.InstanceId);
                break;
            case InstanceCompletedCleanupEvent e:
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.InstanceId);
                break;
            case InstanceFaultedCleanupEvent e:
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.InstanceId);
                break;
            case InstanceSubStateChangedEvent e:
                // "The" instance for this event is the parent — the subflow only reports through it.
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.ParentInstanceId, e.ParentInstanceId, e.SubInstanceId);
                break;
            case InstanceSubCompletedEvent e:
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.InstanceId, e.InstanceId, e.SubInstanceId);
                break;
            case InstanceSubFaultedEvent e:
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.InstanceId, e.InstanceId, e.SubInstanceId);
                break;
            case InstanceSubCanceledEvent e:
                AddInstanceIdentity(tags, e.Domain, e.Flow, e.InstanceId, e.InstanceId, e.SubInstanceId);
                break;
        }

        return tags;
    }

    private static void StampDaprCallback(Activity? activity, ActivityContext ambientContext)
    {
        if (activity is null || ambientContext == default ||
            ambientContext.TraceId == activity.TraceId)
        {
            return;
        }

        activity.SetTag(TelemetryConstants.TagNames.DaprCallback, true);
        activity.SetTag(
            TelemetryConstants.TagNames.DaprCallbackTraceId, ambientContext.TraceId.ToString());
        activity.SetTag(
            TelemetryConstants.TagNames.DaprCallbackSpanId, ambientContext.SpanId.ToString());
    }

    private static void AddInstanceIdentity(
        List<KeyValuePair<string, object?>> tags,
        string domain,
        string flow,
        Guid instanceId,
        Guid? parentInstanceId = null,
        Guid? subflowInstanceId = null)
    {
        tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Domain, domain));
        tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.Flow, flow));
        tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.InstanceId, instanceId));
        if (parentInstanceId.HasValue)
        {
            tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.ParentInstanceId, parentInstanceId.Value));
        }
        if (subflowInstanceId.HasValue)
        {
            tags.Add(new KeyValuePair<string, object?>(TelemetryConstants.TagNames.SubflowInstanceId, subflowInstanceId.Value));
        }
    }

    public void Dispose()
    {
        _laneScope?.Dispose();
        _activity?.Dispose();
        if (_restoreAmbient)
        {
            // A forced-root span's Parent (the local Activity object reference) is null, so
            // Activity.Stop() — invoked by the Dispose() above — sets Activity.Current to null
            // instead of restoring it. Put back whatever was ambient before Start() cleared it.
            Activity.Current = _ambientToRestore;
        }
        _correlationChange?.Dispose();
    }
}
