using BBT.Aether.Events;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a parent instance faults, requesting its active SubFlow children to fault as well.
/// Mirrors the <see cref="ChildSubflowCancelRequestedEvent"/> pattern for downward fault propagation.
/// </summary>
[EventName("instance.faulted.child")]
public class ChildSubflowFaultRequestedEvent : IDistributedEvent, ITraceableDistributedEvent
{
    /// <summary>
    /// The ID of the child SubFlow instance that should be faulted
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// The ID of the parent instance that faulted
    /// </summary>
    public required Guid ParentInstanceId { get; init; }

    /// <summary>
    /// The domain of the child SubFlow
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the child SubFlow
    /// </summary>
    public required string Flow { get; init; }

    /// <summary>
    /// The version of the child SubFlow workflow
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// When the parent instance faulted
    /// </summary>
    public required DateTime FaultedAt { get; init; }

    /// <summary>
    /// The root ancestor instance ID for nested subflow chains.
    /// <c>null</c> when this is a root (non-subflow) instance.
    /// </summary>
    public Guid? RootInstanceId { get; init; }

    /// <summary>
    /// Typed context identifying the terminal cascade that requested this fault.
    /// </summary>
    public TerminationContext Termination { get; init; } = default!;

    /// <summary>W3C traceparent captured at publish time (stamped centrally by the event bus).</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate accompanying <see cref="TraceParent"/>.</summary>
    public string? TraceState { get; set; }

    /// <summary>Originating request id (X-Request-Id value) for log correlation.</summary>
    public string? CorrelationId { get; set; }
}
