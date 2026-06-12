using BBT.Aether.Domain.Repositories;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Repository for managing InstanceTask entities.
/// </summary>
public interface IInstanceTaskRepository : IRepository<InstanceTask, Guid>
{
    /// <summary>
    /// Gets all instance tasks for a specific transition.
    /// </summary>
    /// <param name="transitionId">The transition ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of instance tasks.</returns>
    Task<List<InstanceTask>> GetByTransitionIdAsync(
        Guid transitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single instance task by its unique identifier.
    /// Non-tracking (AsNoTracking) — intended for read-only monitoring queries only.
    /// </summary>
    /// <param name="id">The task entity ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching task, or null if none exists.</returns>
    Task<InstanceTask?> GetByIdAsReadOnlyAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the IDs of completed tasks for a specific transition.
    /// Used for retry bypass logic to skip already completed tasks.
    /// </summary>
    /// <param name="transitionId">The transition ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of completed task IDs (TaskId property).</returns>
    Task<List<string>> GetCompletedTaskIdsAsync(
        Guid transitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the IDs of tasks with a specific status for a transition.
    /// </summary>
    /// <param name="transitionId">The transition ID.</param>
    /// <param name="status">The task status to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of task IDs with the specified status.</returns>
    Task<List<string>> GetTaskIdsByStatusAsync(
        Guid transitionId,
        Definitions.TaskStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the IDs of tasks that completed with business success.
    /// Used for retry bypass logic to skip tasks that already succeeded at business level.
    /// </summary>
    /// <param name="transitionId">The transition ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of task IDs with BusinessStatus.Success.</returns>
    Task<List<string>> GetSuccessfulTaskIdsAsync(
        Guid transitionId,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only: per-task execution aggregation across the current schema (additive, monitor-only).</summary>
    Task<List<TaskExecutionStat>> GetTaskStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: returns all tasks belonging to the given instance, joined with their parent
    /// transition's definition key and state context. Ordered by <c>StartedAt</c> ascending.
    /// Additive — monitor-only.
    /// </summary>
    Task<List<InstanceTaskRow>> GetByInstanceIdAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);
}
