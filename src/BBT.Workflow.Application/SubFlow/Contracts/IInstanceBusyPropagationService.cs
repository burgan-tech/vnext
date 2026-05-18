using BBT.Workflow.Gateway;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Marks an instance as Busy and recursively propagates to active SubFlow correlations
/// via gateway routing. Handles cross-domain SubFlow chains by delegating recursive
/// calls through <see cref="IInstanceCommandGateway"/>.
/// </summary>
public interface IInstanceBusyPropagationService
{
    /// <summary>
    /// Marks the target instance as Busy and recursively propagates to its active SubFlow.
    /// Idempotent: skips marking if the instance is already Busy or Completed.
    /// </summary>
    /// <param name="input">Target domain, workflow, instance id, and optional version.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkBusyAsync(MarkBusyInput input, CancellationToken cancellationToken = default);
}
