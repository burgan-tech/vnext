using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Evaluation;
using BBT.Workflow.Tasks.Evaluators;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Factory;
using BBT.Workflow.Tasks.Persistence;
using BBT.Workflow.Tasks.Persistence.Strategies;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using IConditionEvaluator = BBT.Workflow.Tasks.Evaluation.IConditionEvaluator;
using ITimerEvaluator = BBT.Workflow.Tasks.Evaluation.ITimerEvaluator;
using TaskFactory = BBT.Workflow.Tasks.Factory.TaskFactory;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering all task-related services in the DI container.
/// Provides a declarative way to configure task execution infrastructure using unified abstractions.
/// </summary>
public static class TaskServiceCollectionExtensions
{
    /// <summary>
    /// Adds all task-related services to the DI container.
    /// This is the main entry point for task configuration.
    /// </summary>
    public static IServiceCollection AddTaskHandlers(this IServiceCollection services)
    {
        // New Executor architecture
        services.AddTaskExecutors();
        
        // Core unified infrastructure
        services.AddTaskEvaluators();
        
        // Task coordination and orchestration
        services.AddTaskCoordination();
        
        // Supporting services
        services.AddTaskFactories();
        services.AddTaskPersistence();
        services.AddScriptingServices();
        
        return services;
    }

    /// <summary>
    /// Adds the new Task Executor architecture services.
    /// Each task type has a dedicated executor that handles the full lifecycle.
    /// </summary>
    private static IServiceCollection AddTaskExecutors(this IServiceCollection services)
    {
        // Remote invoker service for Dapr invocation
        services.TryAddScoped<IRemoteInvokerService, RemoteInvokerService>();
        
        // Executor registry
        services.TryAddScoped<ITaskExecutorRegistry, TaskExecutorRegistry>();
        
        // Script executor (no remote - inline execution)
        services.AddTaskExecutor<ScriptTaskExecutor>();
        
        // Human task executor (no remote - state change only)
        services.AddTaskExecutor<HumanTaskExecutor>();
        
        // HTTP and Dapr remote executors
        services.AddTaskExecutor<HttpTaskExecutor>();
        services.AddTaskExecutor<DaprServiceTaskExecutor>();
        services.AddTaskExecutor<DaprBindingTaskExecutor>();
        services.AddTaskExecutor<DaprHttpEndpointTaskExecutor>();
        services.AddTaskExecutor<DaprPubSubTaskExecutor>();
        
        // Notification task executor (uses Dapr binding with runtime-resolved component)
        services.AddTaskExecutor<NotificationTaskExecutor>();
        
        // Trigger task executors (domain-aware: local or remote)
        services.AddTaskExecutor< SubProcessTaskExecutor>();
        services.AddTaskExecutor<StartTriggerTaskExecutor>();
        services.AddTaskExecutor<DirectTriggerTaskExecutor>();
        services.AddTaskExecutor<GetInstanceDataTaskExecutor>();
        
        return services;
    }

    /// <summary>
    /// Adds unified task evaluators for special tasks (Condition, Timer).
    /// Evaluators implement ITaskEvaluator&lt;T&gt; for type-safe evaluation.
    /// </summary>
    private static IServiceCollection AddTaskEvaluators(this IServiceCollection services)
    {
        // Evaluator implementations
        services.AddScoped<IConditionEvaluator, ScriptConditionEvaluator>();
        services.AddScoped<ITimerEvaluator, ScriptTimerEvaluator>();
        
        // Unified evaluator registry
        services.AddScoped<ITaskEvaluatorRegistry, TaskEvaluatorRegistry>();
        
        return services;
    }

    /// <summary>
    /// Adds task coordination services (ITaskCoordinator, ITaskConditionService, ITaskTimerService).
    /// </summary>
    private static IServiceCollection AddTaskCoordination(this IServiceCollection services)
    {
        services.AddScoped<ITaskCoordinator, TaskCoordinator>();
        services.AddScoped<ITaskConditionService, TaskCoordinator>();
        services.AddScoped<ITaskTimerService, TaskCoordinator>();
        
        return services;
    }

    /// <summary>
    /// Adds task factory services with configuration-driven selection.
    /// Both TaskFactory and PooledTaskFactory are stateless or use thread-safe shared state,
    /// so they are registered as Singleton for optimal performance.
    /// </summary>
    private static IServiceCollection AddTaskFactories(this IServiceCollection services)
    {
        // Configure TaskFactory options from configuration
        services.AddOptions<TaskFactoryOptions>()
            .BindConfiguration(TaskFactoryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register TaskFactory as Singleton (stateless - no mutable state)
        services.AddSingleton<TaskFactory>();

        // Register PooledTaskFactory as Singleton (thread-safe ConcurrentDictionary for pools)
        services.AddSingleton<PooledTaskFactory>();

        // Register ITaskFactory with configuration-driven selection
        services.AddSingleton<ITaskFactory>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TaskFactoryOptions>>();

            return options.Value.UseObjectPooling
                ? serviceProvider.GetRequiredService<PooledTaskFactory>()
                : serviceProvider.GetRequiredService<TaskFactory>();
        });

        return services;
    }

    /// <summary>
    /// Adds task persistence strategies.
    /// </summary>
    private static IServiceCollection AddTaskPersistence(this IServiceCollection services)
    {
        services.AddScoped<ITaskPersistenceStrategy, StandardTaskPersistenceStrategy>();
        services.AddScoped<ITaskPersistenceStrategy, ExtensionTaskPersistenceStrategy>();
        services.AddScoped<ITaskPersistenceStrategyFactory, TaskPersistenceStrategyFactory>();
        
        return services;
    }

    /// <summary>
    /// Adds scripting services for script execution context.
    /// CSharpEvaluator uses collectible AssemblyLoadContext to prevent memory leaks
    /// from dynamic script compilation - assemblies can be GC'd when no longer referenced.
    /// </summary>
    private static IServiceCollection AddScriptingServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IScriptContextFactory, ScriptContextFactory>();
        
        // Evaluator is stateless - singleton for efficiency (caches MetadataReferences only)
        services.TryAddSingleton<IEvaluator, CSharpEvaluator>();
        
        services.TryAddScoped<IScriptEngine, ScriptEngine>();
        
        return services;
    }

    /// <summary>
    /// Registers a custom task executor for a specific task type.
    /// Use this to extend the system with custom executors.
    /// </summary>
    /// <typeparam name="TExecutor">The executor type implementing ITaskExecutor.</typeparam>
    public static IServiceCollection AddTaskExecutor<TExecutor>(this IServiceCollection services)
        where TExecutor : class, ITaskExecutor
    {
        services.AddScoped<ITaskExecutor, TExecutor>();
        return services;
    }

    /// <summary>
    /// Registers a custom task executor with a factory function.
    /// </summary>
    public static IServiceCollection AddTaskExecutor(
        this IServiceCollection services,
        Func<IServiceProvider, ITaskExecutor> implementationFactory)
    {
        services.AddScoped(implementationFactory);
        return services;
    }
}
