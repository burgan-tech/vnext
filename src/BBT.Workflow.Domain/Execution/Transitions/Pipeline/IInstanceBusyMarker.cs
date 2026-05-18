namespace BBT.Workflow.Execution.Pipeline;

/// <summary>
/// Marks an instance as Busy immediately after lock acquisition,
/// before the pipeline steps begin. This ensures any concurrent reader
/// sees the instance as in-progress from the moment the lock is held.
/// </summary>
public interface IInstanceBusyMarker
{
    /// <summary>
    /// Sets the instance status to Busy and persists in an isolated unit of work.
    /// No-op if the instance is already Busy, Completed, or in a SubFlow resume path.
    /// </summary>
    /// <param name="instanceId">The workflow instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkBusyAsync(Guid instanceId, CancellationToken cancellationToken = default);
}
