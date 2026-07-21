using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Data payload for propagating a canceled SubItem outcome to its parent.
/// </summary>
public record SubItemCanceledInput
{
    public required Guid InstanceId { get; init; }
    public required Guid SubInstanceId { get; init; }
    public required string Domain { get; init; }
    public required string Flow { get; init; }
    public string? Version { get; init; }
    public required string CanceledState { get; init; }
    public required DateTime CanceledAt { get; init; }
    public Guid? RootInstanceId { get; init; }
    public bool Sync { get; init; }
    public TerminationContext? Termination { get; init; }
}
