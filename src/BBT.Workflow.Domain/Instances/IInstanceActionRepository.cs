using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

/// <summary>
/// Read-only repository for <see cref="InstanceAction"/> entities.
/// Additive — monitor-only.
/// </summary>
public interface IInstanceActionRepository : IRepository<InstanceAction, Guid>
{
    /// <summary>
    /// Returns all actions for the given task, ordered by StartedAt ascending.
    /// Returns an empty list when the task has no recorded actions.
    /// </summary>
    /// <param name="taskId">The parent task identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered list of actions; empty if none exist.</returns>
    Task<List<InstanceAction>> GetByTaskIdAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}
