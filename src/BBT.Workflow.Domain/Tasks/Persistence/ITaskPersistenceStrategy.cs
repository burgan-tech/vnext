using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;

namespace BBT.Workflow.Tasks.Persistence;

/// <summary>
/// Strategy interface for handling task persistence based on different trigger types.
/// This interface enables different persistence behaviors for various TaskTrigger scenarios
/// while maintaining separation of concerns and following SOLID principles.
/// </summary>
/// <remarks>
/// Persistence operations are designed to be non-blocking - failures are logged but don't
/// interrupt the task execution flow. This design choice reflects the resilient nature of
/// workflow execution where persistence failures should not cause task failures.
/// </remarks>
public interface ITaskPersistenceStrategy
{
    /// <summary>
    /// Determines if this strategy should handle the given task execution context.
    /// </summary>
    /// <param name="origin">The component that initiated task execution.</param>
    /// <returns>True if this strategy should handle the task, false otherwise.</returns>
    bool CanHandle(TaskExecutionOrigin origin);

    /// <summary>
    /// Handles the creation and initial persistence of an InstanceTask if required.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be persisted.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    Task<InstanceTask> HandleCreationAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles the completion and final persistence of an InstanceTask if required.
    /// </summary>
    /// <param name="instanceTask">The InstanceTask to be updated.</param>
    /// <param name="cancellationToken">Cancellation token for async operation control.</param>
    Task HandleCompletionAsync(InstanceTask instanceTask, CancellationToken cancellationToken = default);
}
