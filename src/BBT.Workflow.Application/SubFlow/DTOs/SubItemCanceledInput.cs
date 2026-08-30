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

    /// <summary>
    /// How many times a terminal-revert has re-published this event as a durable-delivery rearm.
    /// <c>null</c>/<c>0</c> for an original delivery. See <c>FlowCompletedInput.RearmAttempt</c>.
    /// </summary>
    public int? RearmAttempt { get; init; }
}
