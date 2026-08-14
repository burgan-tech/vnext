using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a workflow instance faults to trigger job cleanup.
/// This event triggers cancellation of all scheduled jobs for the faulted instance.
/// </summary>
/// <remarks>
/// This event supports hooks. Register hooks via DI:
/// <code>
/// services.AddEventHook&lt;InstanceFaultedCleanupEvent, InstanceFaultedCleanupEventHook&gt;();
/// </code>
/// </remarks>
[EventHook]
[EventName("instance.faulted.cleanup")]
public class InstanceFaultedCleanupEvent : IDistributedEvent, ITraceableDistributedEvent
{
    /// <summary>
    /// The ID of the faulted instance
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// The domain of the faulted instance
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the faulted instance
    /// </summary>
    public required string Flow { get; init; }
    
    /// <summary>
    /// The workflow version of the faulted instance
    /// </summary>
    public required string Version { get; init; }
    
    /// <summary>
    /// When the instance faulted
    /// </summary>
    public required DateTime FaultedAt { get; init; }

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
    public string? CorrelationId { get; set; }
}
