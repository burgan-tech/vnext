namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for handling upward SubFlow fault propagation.
/// When a SubFlow faults, this service notifies the parent instance by recording
/// the child's incident and faulting the parent.
/// </summary>
public interface ISubflowFaultService
{
    /// <summary>
    /// Handles SubFlow fault by recording the child's incident on the parent and faulting the parent instance.
    /// Idempotent: skips processing if the parent is already Faulted or Completed.
    /// </summary>
    /// <param name="input">The faulted SubFlow data including incident details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task FaultAsync(SubFlowFaultedInput input, CancellationToken cancellationToken = default);
}
