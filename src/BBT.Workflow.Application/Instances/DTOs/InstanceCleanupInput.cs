namespace BBT.Workflow.Instances;

/// <summary>
/// Body of the internal <c>cancel-cleanup</c> endpoint the Inbox forwards the terminal cleanup events to.
/// </summary>
public sealed class InstanceCleanupInput
{
    /// <summary>
    /// The fault's <c>FaultedAt</c>, sent only for a faulted-cleanup. With it the cleanup runs only while the
    /// instance still rests in that fault (vnext#1252); without it — completed and canceled cleanups, an older
    /// Inbox — it runs as before.
    /// </summary>
    public DateTime? FaultedAt { get; init; }
}
