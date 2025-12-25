using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Tasks.Executors;

/// <summary>
/// Registry for resolving task executors based on task type.
/// Uses DI-injected IEnumerable to discover all registered executors.
/// Caches executors in a dictionary for O(1) lookup.
/// </summary>
/// <remarks>
/// This registry is responsible only for executor resolution.
/// Error boundary and retry capabilities are applied by ITaskExecutorInvoker.
/// </remarks>
public sealed class TaskExecutorRegistry : ITaskExecutorRegistry
{
    private readonly IReadOnlyDictionary<TaskType, ITaskExecutor> _executorMap;
    private readonly ILogger<TaskExecutorRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of TaskExecutorRegistry.
    /// Builds a dictionary cache for O(1) executor lookup.
    /// </summary>
    public TaskExecutorRegistry(
        IEnumerable<ITaskExecutor> executors,
        ILogger<TaskExecutorRegistry> logger)
    {
        _logger = logger;
        _executorMap = executors.ToDictionary(e => e.TaskType);
        
        _logger.LogDebug("TaskExecutorRegistry initialized with {Count} executors: {Types}",
            _executorMap.Count,
            string.Join(", ", _executorMap.Keys));
    }

    /// <inheritdoc />
    public Result<ITaskExecutor> GetExecutor(TaskType taskType)
    {
        if (_executorMap.TryGetValue(taskType, out var executor))
        {
            _logger.LogDebug("Resolved executor {ExecutorType} for task type {TaskType}",
                executor.GetType().Name, taskType);
            return Result<ITaskExecutor>.Ok(executor);
        }

        _logger.LogWarning("No executor found for task type {TaskType}", taskType);
        return Result<ITaskExecutor>.Fail(Error.NotFound(
            WorkflowErrorCodes.TaskExecution,
            $"No executor registered for task type: {taskType}"));
    }

    /// <inheritdoc />
    public bool HasExecutor(TaskType taskType)
    {
        return _executorMap.ContainsKey(taskType);
    }
}
