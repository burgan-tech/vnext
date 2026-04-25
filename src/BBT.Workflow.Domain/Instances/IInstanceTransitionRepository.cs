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
    /// Gets all transitions for an instance ordered by <see cref="InstanceTransition.StartedAt"/> ascending.
    /// </summary>
    /// <param name="instanceId">The instance ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of transitions for the instance.</returns>
    Task<List<InstanceTransition>> GetByInstanceIdAsync(Guid instanceId, CancellationToken cancellationToken = default);
}