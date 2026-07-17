using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;

namespace BBT.Workflow.Instances.Events;

/// <summary>
/// Event published when a SubFlow or SubProcess instance is canceled, notifying the parent instance.
/// </summary>
[EventHook(EventHookMode.DurablePostCommit)]
[EventName("instance.sub.canceled")]
public class InstanceSubCanceledEvent : IDistributedEvent
{
    /// <summary>The ID of the parent instance.</summary>
    [EventSubject]
    public required Guid InstanceId { get; init; }

    /// <summary>The domain of the parent workflow.</summary>
    public required string Domain { get; init; }

    /// <summary>The workflow name of the parent.</summary>
    public required string Flow { get; init; }

    /// <summary>The version of the parent workflow.</summary>
    public required string? Version { get; init; }

    /// <summary>The ID of the canceled SubItem instance.</summary>
    public required Guid SubInstanceId { get; init; }

    /// <summary>The state the SubItem was in when it was canceled.</summary>
    public required string CanceledState { get; init; }

    /// <summary>When the SubItem was canceled.</summary>
    public required DateTime CanceledAt { get; init; }

    /// <summary>The root ancestor instance ID for nested SubItem chains.</summary>
    public Guid? RootInstanceId { get; init; }

    /// <summary>The kind of canceled SubItem.</summary>
    public required SubItemType SubItemType { get; init; }

    /// <summary>Whether the canceling pipeline chain had a synchronous caller.</summary>
    public bool Sync { get; init; }

    /// <summary>The origin of this terminal operation.</summary>
    public required TerminationOrigin TerminationOrigin { get; init; }

    /// <summary>The instance that initiated the terminal cascade.</summary>
    public required Guid InitiatorInstanceId { get; init; }

    /// <summary>The identifier shared by every operation in the terminal cascade.</summary>
    public required Guid CascadeId { get; init; }
}
