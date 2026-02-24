using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

public interface IInstanceTransitionRepository : IRepository<InstanceTransition, Guid>
{
    /// <summary>
    /// Gets all transition records for the given instance, ordered by start time.
    /// </summary>
    /// <param name="instanceId">The instance identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of instance transitions.</returns>
    Task<List<InstanceTransition>> GetListByInstanceIdAsync(Guid instanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a transition to mark it as completed.
    /// </summary>
    /// <param name="transition">The transition to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateCompletedAsync(InstanceTransition transition, CancellationToken cancellationToken);
}