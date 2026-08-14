using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;
using BBT.Workflow.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a workflow instance is canceled.
/// Contains all necessary information about the canceled instance.
/// </summary>
/// <remarks>
/// This event supports hooks. Register hooks via DI:
/// <code>
/// services.AddEventHook&lt;InstanceCanceledEvent, InstanceCanceledEventHook&gt;();
/// </code>
/// </remarks>
[EventHook]
[EventName("instance.canceled")]
public class InstanceCanceledEvent : IDistributedEvent, ITraceableDistributedEvent
{
    /// <summary>
    /// The ID of the canceled instance
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>
    /// The domain of the canceled instance
    /// </summary>
    public required string Domain { get; init; }

    /// <summary>
    /// The workflow name of the canceled instance
    /// </summary>
    public required string Flow { get; init; }
    
    /// <summary>
    /// The workflow version of the canceled instance
    /// </summary>
    public required string Version { get; init; }
    
    /// <summary>
    /// The state where the instance was canceled
    /// </summary>
    public required string CanceledState { get; init; }
    
    /// <summary>
    /// When the instance was canceled
    /// </summary>
    public required DateTime CanceledAt { get; init; }
    
    /// <summary>
    /// Duration of the instance execution before cancellation
    /// </summary>
    public TimeSpan? Duration { get; init; }

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
