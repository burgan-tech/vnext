using BBT.Workflow.Definitions;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Tasks;

/// <summary>
/// Defines a factory contract for creating appropriate task executors based on task type.
/// This factory abstracts the creation and management of different executor implementations.
/// </summary>
public interface ITaskExecutorFactory
{
    /// <summary>
    /// Gets the appropriate task executor for the specified task type.
    /// </summary>
    /// <param name="type">The type of workflow task that needs to be executed.</param>
    /// <returns>An instance of ITaskExecutor that can handle the specified task type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the task type is not supported or recognized.</exception>
    ITaskExecutor GetExecutor(TaskType type);
}

/// <summary>
/// Factory implementation for creating task executors based on task type using dependency injection.
/// This factory resolves the appropriate executor from the service container for each supported task type.
/// </summary>
/// <param name="serviceProvider">The service provider used to resolve executor instances from the DI container.</param>
public sealed class TaskExecutorFactory(IServiceProvider serviceProvider) : ITaskExecutorFactory
{
    /// <summary>
    /// Gets the appropriate task executor for the specified task type by resolving it from the service container.
    /// </summary>
    /// <param name="type">The type of workflow task that determines which executor to return.</param>
    /// <returns>An instance of ITaskExecutor configured for the specified task type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the task type is not supported by any registered executor.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the required executor service is not registered in the DI container.</exception>
    public ITaskExecutor GetExecutor(TaskType type)
    {
        switch (type)
        {
            case TaskType.Http:
                return serviceProvider.GetRequiredService<HttpTaskExecutor>();
            case TaskType.DaprHttpEndpoint:
                return serviceProvider.GetRequiredService<DaprHttpEndpointTaskExecutor>();
            case TaskType.DaprBinding:
                return serviceProvider.GetRequiredService<DaprBindingTaskExecutor>();
            case TaskType.DaprService:
                return serviceProvider.GetRequiredService<DaprServiceTaskExecutor>();
            case TaskType.DaprPubSub:
                return serviceProvider.GetRequiredService<DaprPubSubTaskExecutor>();
            case TaskType.Script:
                return serviceProvider.GetRequiredService<ScriptTaskExecutor>();
            case TaskType.Human:
                return serviceProvider.GetRequiredService<DaprHumanTaskExecutor>();
            case TaskType.Condition:
                return serviceProvider.GetRequiredService<ConditionTaskExecutor>();
            case TaskType.Timer:
                return serviceProvider.GetRequiredService<TimerTaskExecutor>();
            case TaskType.SignalR:
                return serviceProvider.GetRequiredService<SignalRTaskExecutor>();
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }
}