using BBT.Aether.Events;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a workflow instance completes to trigger job cleanup.
/// This event triggers cancellation of all scheduled jobs for the completed instance.
/// </summary>
[EventName("instance.completed.cleanup")]
public class InstanceCompletedCleanupEvent : IDistributedEvent, ITraceableDistributedEvent
{
    /// <summary>
    /// The ID of the completed instance
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// The domain of the completed instance
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the completed instance
    /// </summary>
    public required string Flow { get; init; }
    
    /// <summary>
    /// The workflow version of the completed instance
    /// </summary>
    public required string Version { get; init; }
    
    /// <summary>
    /// When the instance was completed
    /// </summary>
    public required DateTime CompletedAt { get; init; }

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
}
