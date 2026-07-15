using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Persistence;

/// <summary>
/// Factory implementation that creates and returns appropriate task persistence strategies
/// based on the TaskTrigger type using dependency injection and the Result pattern.
/// </summary>
/// <remarks>
/// This factory uses a collection of registered strategies and selects the first one
/// that can handle the given TaskTrigger. The factory pattern provides flexibility
/// for adding new strategies without modifying existing code. Returns Result instead
/// of throwing exceptions following Railway Oriented Programming principles.
/// </remarks>
public sealed class TaskPersistenceStrategyFactory(
    IEnumerable<ITaskPersistenceStrategy> strategies) : ITaskPersistenceStrategyFactory
{
    private readonly IReadOnlyList<ITaskPersistenceStrategy> _strategies = strategies.ToList();

    /// <summary>
    /// Gets the appropriate task persistence strategy for the given TaskTrigger.
    /// </summary>
    /// <param name="origin">The execution origin that determines which strategy to use.</param>
    /// <returns>
    /// A Result containing the strategy instance that can handle the specified TaskTrigger,
    /// or a failure result with TaskPersistenceStrategyNotFound error if no strategy is available.
    /// </returns>
    public Result<ITaskPersistenceStrategy> GetStrategy(TaskExecutionOrigin origin)
    {
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(origin));

        return strategy is not null
            ? Result<ITaskPersistenceStrategy>.Ok(strategy)
            : Result<ITaskPersistenceStrategy>.Fail(
                Error.NotFound(
                    WorkflowErrorCodes.TaskPersistenceStrategyNotFound,
                    $"No task persistence strategy found for origin: {origin}",
                    origin.ToString()));
    }
}
