using BBT.Aether.Events;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a SubFlow instance changes state.
/// Contains all necessary information about the state change for parent instance synchronization.
/// </summary>
[EventName("instance.sub.state.changed")]
public class InstanceSubStateChangedEvent : IDistributedEvent, ITraceableDistributedEvent
{
    /// <summary>
    /// The ID of the Parent instance
    /// </summary>
    [EventSubject]
    public required Guid ParentInstanceId { get; init; }

    /// <summary>
    /// The ID of the SubFlow instance that changed state
    /// </summary>
    public required Guid SubInstanceId { get; init; }

    /// <summary>
    /// The domain of the parent workflow
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the parent
    /// </summary>
    public required string Flow { get; init; }

    /// <summary>
    /// The version of the parent workflow
    /// </summary>
    public required string? Version { get; init; }

    /// <summary>
    /// The new state after the change
    /// </summary>
    public required string NewState { get; init; }

    /// <summary>
    /// The previous state before the change
    /// </summary>
    public required string? PreviousState { get; init; }

    /// <summary>
    /// Type of the new state
    /// Used for upward propagation to parent instance
    /// </summary>
    public required int NewStateType { get; init; }
    
    /// <summary>
    /// Subtype of the new state
    /// Used for upward propagation to parent instance and automated status handling
    /// </summary>
    public required int NewStateSubType { get; init; }

    /// <summary>
    /// When the state change occurred
    /// </summary>
    public required DateTime ChangedAt { get; init; }

    /// <summary>
    /// The root ancestor instance ID for nested subflow chains.
    /// <c>null</c> when this is a root (non-subflow) instance.
    /// </summary>
    public Guid? RootInstanceId { get; init; }


    /// <summary>W3C traceparent captured at publish time (stamped centrally by the event bus).</summary>
    public string? TraceParent { get; set; }

    /// <summary>W3C tracestate accompanying <see cref="TraceParent"/>.</summary>
    public string? TraceState { get; set; }

    /// <summary>Originating request id (X-Request-Id value) for log correlation.</summary>
    public string? RequestId { get; set; }

    public override string ToString()
    {
        return $"{nameof(InstanceSubStateChangedEvent)}: ParentInstanceId={ParentInstanceId} SubInstanceId={SubInstanceId} Domain={Domain} Flow={Flow} PreviousState={PreviousState} NewState={NewState}";
    }
}
