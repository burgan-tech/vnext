using BBT.Aether.Results;

namespace BBT.Workflow.Instances;

/// <summary>
/// Service for handling instance cancellation operations.
/// Processes job cleanup when an instance is canceled.
/// </summary>
public interface IInstanceCancellationService
{
    /// <summary>
    /// Processes cancellation for an instance by cleaning up active jobs.
    /// </summary>
    /// <param name="instanceId">The ID of the canceled instance.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure of the cancellation processing.</returns>
    Task<Result> ProcessCancellationAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Processes cancellation for specific state transitions by cleaning up their jobs.
    /// Only cancels jobs that match the provided transition keys.
    /// </summary>
    /// <param name="instanceId">The ID of the instance.</param>
    /// <param name="sourceState">
    /// The source-state key that owns the jobs to cancel (scopes the match so a same-named transition
    /// on another state's timer is not cancelled). Pass <c>null</c> for jobs without source-state
    /// scoping (e.g. the long-poll-ack fallback).
    /// </param>
    /// <param name="transitionKeys">List of transition keys whose jobs should be canceled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A result indicating success or failure of the cancellation processing.</returns>
    Task<Result> ProcessStateTransitionsCancellationAsync(
        Guid instanceId,
        string? sourceState,
        IReadOnlyList<string> transitionKeys,
        CancellationToken cancellationToken = default);
}

