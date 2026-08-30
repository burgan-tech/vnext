namespace BBT.Workflow.Events;

/// <summary>
/// Distributed events that carry W3C trace context and the originating request id across the
/// outbox → pub/sub → inbox hop, so consumers can continue the publisher's trace instead of
/// starting a disconnected one and can keep log correlation to the client request.
/// Properties are settable so <c>TraceStampingDistributedEventBus</c> can stamp them centrally at
/// publish time (while the publisher's Activity is still ambient) without every raise site
/// having to care about diagnostics.
/// </summary>
public interface ITraceableDistributedEvent
{
    /// <summary>W3C traceparent captured at publish time.</summary>
    string? TraceParent { get; set; }

    /// <summary>W3C tracestate accompanying <see cref="TraceParent"/>.</summary>
    string? TraceState { get; set; }

    /// <summary>Originating request id (X-Request-Id value) for log correlation.</summary>
    string? RequestId { get; set; }
}
