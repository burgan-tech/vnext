using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;

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
public class InstanceCanceledEvent : IDistributedEvent
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
}

