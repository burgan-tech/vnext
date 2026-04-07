using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceTransitionRepository : IRepository<InstanceTransition, Guid>
{
    /// <summary>
    /// Updates a transition to mark it as completed.
    /// </summary>
    /// <param name="transition">The transition to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateCompletedAsync(InstanceTransition transition, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the latest incomplete (not completed) transition for an instance.
    /// Used for retry operations to find the faulted transition.
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The latest incomplete transition, or null if none found.</returns>
    Task<InstanceTransition?> GetLatestIncompleteAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent completed manual transition for an instance.
    /// Used to resolve $PreviousUser for instance authorization (CreatedBy of that transition).
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last completed manual transition, or null if none found.</returns>
    Task<InstanceTransition?> GetLastCompletedManualTransitionAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all transitions for the given instance ordered by <see cref="InstanceTransition.StartedAt"/> ascending.
    /// Non-tracking (AsNoTracking) — intended for read-only monitoring queries only.
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of transitions; empty if none exist.</returns>
    Task<List<InstanceTransition>> GetByInstanceIdAsReadOnlyAsync(Guid instanceId, CancellationToken cancellationToken = default);
}