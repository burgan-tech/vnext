using BBT.Aether.Events;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a workflow instance is canceled.
/// Contains all necessary information about the canceled instance.
/// </summary>
[EventName("instance.canceled.child")]
public class ChildSubflowCancelRequestedEvent: IDistributedEvent
{
    /// <summary>
    /// The ID of the child instance
    /// </summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }
    
    /// <summary>
    /// Parent Instance ID
    /// </summary>
    public required Guid ParentInstanceId { get; init; }
    
    /// <summary>
    /// Sub Flow Domain
    /// </summary>
    public required string Domain { get; init; }
    
    /// <summary>
    /// Sub Flow Name
    /// </summary>
    public required string Flow { get; init; }
    
    /// <summary>
    /// Sub Flow Version
    /// </summary>
    public string? Version { get; set; }
    
    /// <summary>
    /// Completed at
    /// </summary>
    public required DateTime CompletedAt { get; init; }

    /// <summary>
    /// The root ancestor instance ID for nested subflow chains.
    /// <c>null</c> when this is a root (non-subflow) instance.
    /// </summary>
    public Guid? RootInstanceId { get; init; }

    /// <summary>
    /// Typed context identifying the terminal cascade that requested this cancellation.
    /// </summary>
    public TerminationContext Termination { get; init; } = default!;
}
