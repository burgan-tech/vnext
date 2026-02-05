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
}