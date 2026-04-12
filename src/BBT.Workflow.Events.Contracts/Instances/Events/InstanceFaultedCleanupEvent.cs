using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;

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
public class InstanceFaultedCleanupEvent : IDistributedEvent
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
}
