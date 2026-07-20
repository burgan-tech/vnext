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
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
    {
        await using var uow = unitOfWorkManager.Begin(new UnitOfWorkOptions
        {
            Scope = UnitOfWorkScopeOption.RequiresNew,
            IsTransactional = true
        });

        var existing = await instanceTaskRepository.FindByTransitionAndTaskAsync(
            instanceTask.TransitionId,
            instanceTask.TaskId,
            cancellationToken);

        var journal = existing ?? await instanceTaskRepository.InsertAsync(instanceTask, true, cancellationToken);
        await uow.CommitAsync(cancellationToken);
        return journal;
    }

    /// <summary>
    /// Handles the completion and final persistence of an InstanceTask to the database.
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
        await instanceTaskRepository.UpdateAsync(instanceTask, true, cancellationToken);
        await uow.CommitAsync(cancellationToken);
    }
}
