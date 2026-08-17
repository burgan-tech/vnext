using System.Diagnostics;
using BBT.Aether.Tracing;
using BBT.Workflow.Events;

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

    private EventTraceScope(Activity? activity, IDisposable? correlationChange)
    {
        _activity = activity;
        _correlationChange = correlationChange;
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

        return new EventTraceScope(activity, correlationChange);
    }

    public void Dispose()
    {
        _activity?.Dispose();
        _correlationChange?.Dispose();
    }
}
