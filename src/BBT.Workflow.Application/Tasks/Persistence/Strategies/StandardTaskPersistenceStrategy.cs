using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Tasks.Persistence.Strategies;

/// <summary>
/// Standard implementation of task persistence strategy that handles database persistence
/// for normal workflow task execution (excludes Extension tasks).
/// </summary>
/// <remarks>
/// This strategy is responsible for persisting InstanceTask entities to the database
/// for all TaskTrigger types except Extension. It ensures proper audit trail and
/// workflow execution history is maintained.
/// </remarks>
public sealed class StandardTaskPersistenceStrategy(
    IInstanceTaskRepository instanceTaskRepository,
    IUnitOfWorkManager unitOfWorkManager) : ITaskPersistenceStrategy
{
    /// <summary>
    /// Determines if this strategy should handle the task persistence.
    /// Returns true for all TaskTrigger types except Extension.
    /// </summary>
    /// <param name="origin">The component that initiated task execution.</param>
    /// <returns>True if the task should be persisted to database, false otherwise.</returns>
    public bool CanHandle(TaskExecutionOrigin origin)
    {
        return origin == TaskExecutionOrigin.Flow;
    }

    /// <summary>
    /// Handles the creation and initial persistence of an InstanceTask to the database.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be inserted into the database.</param>
    /// <param name="taskTrigger">
    /// The hook this occurrence runs under. Folded into the idempotency probe's key alongside
    /// <paramref name="order"/> so the probe targets this specific occurrence, not just the
    /// (transition, task) pair — see <see cref="InstanceTask.ExecutionKey"/>.
    /// </param>
    /// <param name="order">The occurrence's execution order within its hook.</param>
    /// <param name="skipLookup">
    /// True when the transition record was freshly inserted by this pipeline run — no journal row
    /// can exist for its id, so the idempotency probe below would be a guaranteed-empty SELECT
    /// per task. Retries pass false and keep the probe, which finds and reuses the previous
    /// attempt's row (including legacy rows without an <c>ExecutionKey</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task<InstanceTask> HandleCreationAsync(
        InstanceTask instanceTask,
        TaskTrigger taskTrigger,
        int order,
        bool skipLookup = false,
        CancellationToken cancellationToken = default)
    {
        await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        var existing = skipLookup
            ? null
            : await instanceTaskRepository.FindByTransitionAndTaskAsync(
                instanceTask.TransitionId,
                instanceTask.TaskId,
                taskTrigger,
                order,
                cancellationToken);

        var journal = existing ?? await instanceTaskRepository.InsertAsync(instanceTask, true, cancellationToken);
        await uow.CommitAsync(cancellationToken);
        return journal;
    }

    /// <summary>
    /// Handles the completion and final persistence of an InstanceTask to the database.
    /// One set-based UPDATE of the completion columns instead of attaching the (detached — it was
    /// created in a different scope) entity and rewriting every column including the jsonb
    /// payloads. Runs once per executed task, so this is the hottest task-journal write.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be updated in the database.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
    {
        await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });
        await instanceTaskRepository.MarkCompletedAsync(instanceTask, cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }
}
