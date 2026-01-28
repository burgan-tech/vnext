using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a workflow instance completes to trigger job cleanup.
/// This event triggers cancellation of all scheduled jobs for the completed instance.
/// </summary>
/// <remarks>
/// This event supports hooks. Register hooks via DI:
/// <code>
/// services.AddEventHook&lt;InstanceCompletedCleanupEvent, InstanceCompletedCleanupEventHook&gt;();
/// </code>
/// </remarks>
[EventHook]
[EventName("instance.completed.cleanup")]
public class InstanceCompletedCleanupEvent : IDistributedEvent
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
    /// When the instance was completed
    /// </summary>
    public required DateTime CompletedAt { get; init; }
}
