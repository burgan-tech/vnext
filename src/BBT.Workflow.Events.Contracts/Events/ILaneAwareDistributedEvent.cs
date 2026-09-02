namespace BBT.Workflow.Events;

/// <summary>
/// Distributed events that additionally carry the <em>trace lane anchor</em>, so the operation the
/// consumer starts lands at the right depth in the trace instead of nesting inside its predecessor.
/// <para>
/// Only events that actually open a top-level operation on the consuming side implement this: the
/// transition continuation (which becomes a <c>TransitionJob.Execute</c> lane item) and the subflow
/// terminal events (which drive a parent resume). Purely informational events stay on
/// <see cref="ITraceableDistributedEvent"/> alone — narrowing the surface keeps the other event
/// contracts untouched and cannot break an out-of-repo implementor.
/// </para>
/// <para>
/// Settable for the same reason as <see cref="ITraceableDistributedEvent"/>: stamped centrally by
/// <c>TraceStampingDistributedEventBus</c> at publish time, while the publisher's lane is still ambient.
/// </para>
/// </summary>
public interface ILaneAwareDistributedEvent : ITraceableDistributedEvent
{
    /// <summary>
    /// Lane anchor (W3C traceparent) of the instance that published the event. Becomes the PARENT of
    /// the consumer's span, while <see cref="ITraceableDistributedEvent.TraceParent"/> is linked as
    /// the predecessor.
    /// </summary>
    string? TraceRoot { get; set; }

    /// <summary>
    /// The enclosing lane's anchor. For a subflow terminal event this is the parent instance's lane,
    /// which is where the resume belongs — a resume is a parent-instance operation, not a subflow one.
    /// </summary>
    string? ParentTraceRoot { get; set; }

    /// <summary>
    /// Start of the activation episode the publisher was executing — the instant the request (or
    /// timer, event, resume) that set the instance in motion was accepted. The consumer's rest
    /// point emits an <c>Instance.Activation</c> span starting here, so a parent resumed by a
    /// subflow's terminal event measures from the child's final request, which is what the client
    /// polling the parent actually waited for. Null from an older publisher ⇒ partial span.
    /// </summary>
    DateTimeOffset? EpisodeStartedAt { get; set; }

    /// <summary>What opened the episode; one of <c>TelemetryConstants.ActivationTriggers</c>.</summary>
    string? EpisodeTrigger { get; set; }

    /// <summary>The transition the episode was triggered with (the first hop's key).</summary>
    string? EpisodeTransitionKey { get; set; }
}
