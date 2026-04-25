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
    IInstanceTaskRepository instanceTaskRepository) : ITaskPersistenceStrategy
{
    /// <summary>
    /// Determines if this strategy should handle the task persistence.
    /// Returns true for all TaskTrigger types except Extension.
    /// </summary>
    /// <param name="taskTrigger">The trigger type that initiated the task execution.</param>
    /// <returns>True if the task should be persisted to database, false otherwise.</returns>
    public bool CanHandle(TaskTrigger taskTrigger)
    {
        return taskTrigger != TaskTrigger.Extension;
    }

    /// <summary>
    /// Handles the creation and initial persistence of an InstanceTask to the database.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be inserted into the database.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
    {
        await instanceTaskRepository.InsertAsync(instanceTask, true, cancellationToken);
    }

    /// <summary>
    /// Handles the completion and final persistence of an InstanceTask to the database.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be updated in the database.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    public async Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default)
    {
        await instanceTaskRepository.UpdateAsync(instanceTask, false, cancellationToken);
    }
}
