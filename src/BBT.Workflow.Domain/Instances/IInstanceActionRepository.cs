using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

/// <summary>
/// Read-only repository for <see cref="InstanceAction"/> entities. Read by the Monitor task detail
/// and the public action-history function; nothing in the runtime writes these rows yet.
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
