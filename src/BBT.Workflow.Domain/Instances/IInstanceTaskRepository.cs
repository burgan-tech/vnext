using BBT.Aether.Domain.Repositories;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

/// <summary>
/// Repository for managing InstanceTask entities.
/// </summary>
public interface IInstanceTaskRepository : IRepository<InstanceTask, Guid>
{
    /// <summary>
    /// Finds the durable journal row for a task OCCURRENCE within a transition — identified by
    /// (transitionId, taskId, taskTrigger, order), not just (transitionId, taskId). A task key can
    /// legitimately appear more than once in the same hook (parallel/sequential re-use) and across
    /// different hooks (onExecute/onEntry/onExit) of the same transition; each occurrence needs its
    /// own probe. The returned entity is tracked so the caller can update the same row on retry.
    /// </summary>
    Task<InstanceTask?> FindByTransitionAndTaskAsync(
        Guid transitionId,
        string taskId,
        TaskTrigger taskTrigger,
        int order,
        CancellationToken cancellationToken = default);

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
    /// Gets all instance tasks for a SET of transitions in one query. Read-only (AsNoTracking),
    /// ordered by <c>StartedAt</c>. Exists so timeline-style readers batch instead of issuing one
    /// query per transition (the Monitor instance timeline used to be an N+1).
    /// </summary>
    /// <param name="transitionIds">The transition IDs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All matching tasks; group by <see cref="InstanceTask.TransitionId"/> caller-side.</returns>
    Task<List<InstanceTask>> GetByTransitionIdsAsync(
        IReadOnlyCollection<Guid> transitionIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a task's completion (or fault) as ONE set-based UPDATE of exactly the columns the
    /// completion path mutates — <c>Status</c>, <c>BusinessStatus</c>, <c>Response</c>,
    /// <c>Request</c>, <c>InvocationResult</c>, <c>FinishedAt</c>, <c>Duration</c> — taken from the
    /// (detached) entity's current values. Replaces attaching the entity and full-row-updating
    /// every column including the jsonb payloads. Bypasses the repository's UpdateAsync override,
    /// so no data-sink fan-out fires (no sink is registered today; wire sinks here if that changes).
    /// </summary>
    /// <param name="instanceTask">The mutated task whose values are written.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task MarkCompletedAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Read-only: per-task execution aggregation across the current schema (additive, monitor-only).
    /// </summary>
    /// <param name="since">
    /// Lower bound on <c>StartedAt</c>. Bounds the aggregation's scan — without it the GROUP BY
    /// reads the whole (append-only, unbounded) table. Null means unbounded, for callers that
    /// explicitly want the all-time view.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<List<TaskExecutionStat>> GetTaskStatsAsync(
        DateTime? since = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: returns all tasks belonging to the given instance, joined with their parent
    /// transition's definition key and state context. Ordered by <c>StartedAt</c> ascending.
    /// Additive — monitor-only.
    /// </summary>
    Task<List<InstanceTaskRow>> GetByInstanceIdAsync(
        Guid instanceId,
        CancellationToken cancellationToken = default);
}
