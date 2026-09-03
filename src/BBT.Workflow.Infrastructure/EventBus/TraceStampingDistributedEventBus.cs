using System.Diagnostics;
using BBT.Aether.Events;
using BBT.Aether.Tracing;
using BBT.Workflow.Events;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.EventBus;

/// <summary>
/// Decorator that stamps W3C trace context, the originating request id, and trace-lane anchors
/// onto traceable events at publish time, then delegates to the inner bus.
/// </summary>
public sealed class TraceStampingDistributedEventBus : IDistributedEventBus
{
    private readonly IDistributedEventBus _inner;
    private readonly ILogger<TraceStampingDistributedEventBus> _logger;
    private readonly ICorrelationIdProvider? _correlationIdProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="TraceStampingDistributedEventBus"/> class.
    /// </summary>
    public TraceStampingDistributedEventBus(
        IDistributedEventBus inner,
        ILogger<TraceStampingDistributedEventBus> logger,
        ICorrelationIdProvider? correlationIdProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _correlationIdProvider = correlationIdProvider;
    }

    /// <summary>
    /// Stamps W3C trace context and the originating request id onto traceable events at publish
    /// time — while the publisher's Activity is still ambient — so consumers on the other side of
    /// the outbox/pub-sub hop can continue the trace. Never overwrites values already set by the
    /// publisher.
    /// </summary>
    private void StampTraceContext(object payload)
    {
        if (payload is not ITraceableDistributedEvent traceable)
        {
            return;
        }

        if (string.IsNullOrEmpty(traceable.TraceParent))
        {
            traceable.TraceParent = Activity.Current?.Id;
            traceable.TraceState = Activity.Current?.TraceStateString;
        }

        traceable.RequestId ??= _correlationIdProvider?.Get();

        // Lane-aware events additionally carry the anchor the CONSUMER must parent its top-level
        // operation to (a transition hop, a parent resume). Same never-overwrite policy: a raise
        // site that already knows the right lane — a subflow terminal event, say — wins.
        if (payload is ILaneAwareDistributedEvent laneAware)
        {
            laneAware.TraceRoot ??= WorkflowTraceLane.Current;
            laneAware.ParentTraceRoot ??= WorkflowTraceLane.ParentLane;

            // The activation episode rides with the lane: the consumer's rest point (a parent
            // resume settling Active) measures from the publisher's original trigger, not from the
            // delivery. Same never-overwrite policy; the fields travel together. TraceRoot is
            // filled independently so carriers produced by an older initializer still preserve
            // the episode's original trace parent.
            if (WorkflowTraceLane.Episode is { } episode)
            {
                if (laneAware.EpisodeStartedAt is null)
                {
                    laneAware.EpisodeStartedAt = episode.StartedAt;
                    laneAware.EpisodeTrigger = episode.Trigger;
                    laneAware.EpisodeTransitionKey = episode.TransitionKey;
                }

                laneAware.EpisodeTraceRoot ??= episode.TraceRoot;
            }
        }
    }

    /// <summary>
    /// Publishes an event with outbox enabled (IEventBus implementation).
    /// </summary>
    public Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        return PublishAsync(payload, subject, useOutbox: true, cancellationToken);
    }

    /// <summary>
    /// Stamps trace context onto the payload and delegates to the inner bus.
    /// </summary>
    public async Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        StampTraceContext(payload);

        await _inner.PublishAsync(payload, subject, useOutbox, cancellationToken);
    }

    /// <summary>
    /// Stamps trace context onto the event and delegates to the inner bus using pre-extracted metadata.
    /// </summary>
    public async Task PublishAsync(
        IDistributedEvent @event,
        EventMetadata metadata,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default)
    {
        if (@event == null)
        {
            throw new ArgumentNullException(nameof(@event));
        }

        StampTraceContext(@event);

        await _inner.PublishAsync(@event, metadata, subject, useOutbox, cancellationToken);
    }

    /// <summary>
    /// Publishes a pre-serialized CloudEventEnvelope directly to the broker.
    /// </summary>
    /// <remarks>
    /// This method bypasses stamping — it is used for replaying already-processed events from the outbox.
    /// </remarks>
    public Task PublishEnvelopeAsync(
        byte[] serializedEnvelope,
        string topicName,
        string pubSubName,
        CancellationToken cancellationToken = default)
    {
        return _inner.PublishEnvelopeAsync(serializedEnvelope, topicName, pubSubName, cancellationToken);
    }
}
