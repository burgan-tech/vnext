using BBT.Aether.Results;
using BBT.Workflow.Instances.Events;

namespace BBT.Workflow.SubFlow;

/// <summary>
/// Service for handling child subflow cancellation operations.
/// Propagates cancellation requests to child subflows.
/// </summary>
public interface IChildSubflowCancellationService
{
    /// <summary>
    /// Cancels a child subflow by transitioning it to the cancel state.
    /// </summary>
    /// <param name="instanceId">The ID of the child instance to cancel.</param>
    /// <param name="domain">The domain of the child subflow.</param>
    /// <param name="flow">The flow key of the child subflow.</param>
    /// <param name="version">The version of the child subflow. Can be null for version-agnostic cancellation.</param>
    /// <param name="termination">The terminal cascade context from the parent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure of the cancellation.</returns>
    Task<Result> CancelChildSubflowAsync(
        Guid instanceId,
        string domain,
        string flow,
        string? version,
        TerminationContext termination,
        CancellationToken cancellationToken = default);
}
