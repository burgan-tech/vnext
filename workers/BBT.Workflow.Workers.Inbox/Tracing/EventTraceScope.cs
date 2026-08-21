using System.Diagnostics;
using BBT.Aether.Tracing;
using BBT.Workflow.Events;
using BBT.Workflow.Logging;

namespace BBT.Workflow.Workers.Inbox.Tracing;

/// <summary>
/// Restores the publisher's trace context for an inbox event handler: the event's TraceParent
/// becomes the real parent of the handler span (so the outbox → pub/sub → inbox hop stays inside
/// the originating trace tree), the ambient pub/sub delivery span is attached as an ActivityLink,
/// and the originating request id is pushed into <see cref="ICorrelationIdProvider"/> for the
/// duration of the handler so outbound forwards can propagate it.
/// Dispose in reverse order of acquisition (activity first, correlation restore last).
/// </summary>
internal sealed class EventTraceScope : IDisposable
{
    /// <summary>
    /// ActivitySource for inbox event-consumption spans. Matched by the
    /// "BBT.Workflow.Workers.*" entry in the worker's Telemetry:Tracing:AdditionalSources.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("BBT.Workflow.Workers.Inbox");

    private readonly Activity? _activity;
    private readonly IDisposable? _correlationChange;
    private readonly IDisposable? _laneScope;

    private EventTraceScope(Activity? activity, IDisposable? correlationChange, IDisposable? laneScope)
    {
        _activity = activity;
        _correlationChange = correlationChange;
        _laneScope = laneScope;
    }

    /// <summary>
    /// Starts a consumer span continuing the event's original trace and scopes the correlation id.
    /// Falls back to the ambient parent when the event carries no or invalid trace context.
    /// </summary>
    /// <param name="activityName">Span name, e.g. "InstanceSubCompleted.Handle".</param>
    /// <param name="evt">The traceable event carrying TraceParent/TraceState/RequestId.</param>
    /// <param name="correlationIdProvider">Optional provider to scope the event's request id into.</param>
    public static EventTraceScope Start(
        string activityName,
        ITraceableDistributedEvent evt,
        ICorrelationIdProvider? correlationIdProvider = null)
    {
        Activity? activity;
        if (!string.IsNullOrEmpty(evt.TraceParent) &&
            ActivityContext.TryParse(evt.TraceParent, evt.TraceState, isRemote: true, out var originalContext))
        {
            IEnumerable<ActivityLink>? links = null;
            var ambient = Activity.Current;
            if (ambient is not null && ambient.Context.TraceId != originalContext.TraceId)
            {
                links = [new ActivityLink(ambient.Context)];
            }

            activity = ActivitySource.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: originalContext,
                tags: null,
                links: links);
        }
        else
        {
            activity = ActivitySource.StartActivity(
                activityName,
                ActivityKind.Consumer,
                parentContext: Activity.Current?.Context ?? default);
        }

        IDisposable? correlationChange = null;
        if (correlationIdProvider is not null && !string.IsNullOrEmpty(evt.RequestId))
        {
            correlationChange = correlationIdProvider.Change(evt.RequestId);
        }

        // Establish the trace lane for the handler body. Lane-aware events carry the publisher's
        // anchor; everything else anchors on this handler span. Without this, work started inside a
        // handler (a relayed transition, a republished event) would anchor on the pub/sub delivery
        // span and detach from the originating request's trace tree.
        var laneScope = evt is ILaneAwareDistributedEvent laneAware
            ? WorkflowTraceLane.Reset(laneAware.TraceRoot, laneAware.ParentTraceRoot)
            : WorkflowTraceLane.Reset(activity?.Id);

        return new EventTraceScope(activity, correlationChange, laneScope);
    }

    public void Dispose()
    {
        _laneScope?.Dispose();
        _activity?.Dispose();
        _correlationChange?.Dispose();
    }
}
