using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Tasks.Persistence;

/// <summary>
/// Factory interface for creating appropriate task persistence strategies based on TaskTrigger types.
/// This factory encapsulates the strategy selection logic and provides a clean abstraction
/// for obtaining the correct persistence behavior using the Result pattern.
/// </summary>
public interface ITaskPersistenceStrategyFactory
{
    /// <summary>
    /// Gets the appropriate task persistence strategy for the given TaskTrigger.
    /// </summary>
    /// <param name="origin">The execution origin that determines which strategy to use.</param>
    /// <returns>
    /// A Result containing the strategy instance that can handle the specified TaskTrigger,
    /// or a failure result with TaskPersistenceStrategyNotFound error if no strategy is available.
    /// </returns>
    Result<ITaskPersistenceStrategy> GetStrategy(TaskExecutionOrigin origin);
}
