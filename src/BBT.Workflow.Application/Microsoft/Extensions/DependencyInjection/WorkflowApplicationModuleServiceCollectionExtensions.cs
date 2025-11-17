using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BBT.Workflow.Extentions;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Tasks.Persistence;
using BBT.Workflow.Tasks.Persistence.Strategies;
using TaskFactory = BBT.Workflow.Tasks.Factory.TaskFactory;
using BBT.Workflow.Functions;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Strategies;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Handlers;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.ReEntry;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Application.Notifications;
using BBT.Workflow.Execution.TriggerTransition;
using BBT.Workflow.Tasks.TriggerTransition;
using BBT.Workflow.Instances.Validation;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up application services in an <see cref="IServiceCollection" />.
/// </summary>
public static class WorkflowApplicationModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds the application module services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddApplicationModule(
        this IServiceCollection services)
    {
        services.AddDomainModule();
        services.AddAetherApplication();
        
        // Add transition pipeline architecture
        services.AddTransitionPipeline();

        // Configure service groups
        services.AddHttpClients();
        services.AddApplicationServices();
        services.AddExecutionServices();
        services.AddCacheServices();
        services.AddTaskServices();
        services.AddCastHandlers();

        return services;
    }

    /// <summary>
    /// Configures HTTP clients for task executors.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    private static void AddHttpClients(this IServiceCollection services)
    {
        // Default HTTP client with SSL validation enabled
        services.AddHttpClient("WorkflowHttpClient", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            MaxConnectionsPerServer = 10,
            UseCookies = false
        });

        // HTTP client with SSL validation disabled
        services.AddHttpClient("WorkflowHttpClient.NoSslValidation", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            MaxConnectionsPerServer = 10,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        });
    }

    /// <summary>
    /// Configures core application services.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    private static void AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<DomainCacheContext>();
        services.AddSingleton<IDomainCacheContext>(serviceProvider => serviceProvider.GetRequiredService<DomainCacheContext>());
        services.AddScoped<IAdminAppService, AdminAppService>();
        services.AddScoped<IInstanceCommandAppService, InstanceCommandAppService>();
        services.AddScoped<IInstanceQueryAppService, InstanceQueryAppService>();
        services.AddScoped<IFunctionAppService, FunctionAppService>();
        services.AddScoped<ITaskCommandAppService, TaskCommandAppService>();
        services.AddScoped<IInstanceExtensionService, InstanceExtensionService>();
        services.AddScoped<ISubflowCompletionService, SubflowCompletionService>();
        
        services.AddScoped<IRuntimeService, RuntimeService>();

        // Notifications
        services.AddScoped<DaprComponentDetector>();
    }

    /// <summary>
    /// Configures workflow execution services.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    private static void AddExecutionServices(this IServiceCollection services)
    {
        // Core execution service
        services.AddScoped<IWorkflowExecutionService, WorkflowExecutionService>();
        
        // Legacy services (to be removed after transition pipeline migration)
        services.AddScoped<IExecutionStrategyFactory, ExecutionStrategyFactory>();
        services.AddScoped<ITransitionStrategy, SyncTransitionStrategy>();
        services.AddScoped<ITransitionStrategy, AsyncTransitionStrategy>();

        // State machine and orchestration services
        services.AddScoped<ITaskOrchestrationService, TaskOrchestrationService>();
        services.AddScoped<ITaskConditionService, TaskOrchestrationService>();
        services.AddScoped<ITaskTimerService, TaskOrchestrationService>();
        services.AddScoped<ISubflowStarter, SubflowStarter>();
        services.AddScoped<ISubflowForwardingService, SubflowForwardingService>();
    }

    /// <summary>
    /// Configures component cache store with metrics.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    private static void AddCacheServices(this IServiceCollection services)
    {
        services.AddSingleton<ComponentCacheStore>();
        services.AddSingleton<IComponentCacheStore>(serviceProvider =>
        {
            var originalStore = serviceProvider.GetRequiredService<ComponentCacheStore>();
            var workflowMetrics = serviceProvider.GetRequiredService<IWorkflowMetrics>();
            var logger = serviceProvider.GetRequiredService<ILogger<MetricsAwareComponentCacheStore>>();
            
            return originalStore.WithMetrics(workflowMetrics, logger);
        });
    }

    /// <summary>
    /// Configures task-related services including factories, executors, and persistence strategies.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    private static void AddTaskServices(this IServiceCollection services)
    {
        // Task Factory Services - Configuration Driven
        ConfigureTaskFactory(services);

        // Task Persistence Strategies
        services.AddScoped<ITaskPersistenceStrategy, StandardTaskPersistenceStrategy>();
        services.AddScoped<ITaskPersistenceStrategy, ExtensionTaskPersistenceStrategy>();
        services.AddScoped<ITaskPersistenceStrategyFactory, TaskPersistenceStrategyFactory>();

        // Task Executors
        services.AddScoped<ITaskExecutorFactory, TaskExecutorFactory>();
        services.AddScoped<HttpTaskExecutor>();
        services.AddScoped<DaprBindingTaskExecutor>();
        services.AddScoped<DaprHttpEndpointTaskExecutor>();
        services.AddScoped<DaprHumanTaskExecutor>();
        services.AddScoped<DaprPubSubTaskExecutor>();
        services.AddScoped<DaprServiceTaskExecutor>();
        services.AddScoped<ScriptTaskExecutor>();
        services.AddScoped<ConditionTaskExecutor>();
        services.AddScoped<TimerTaskExecutor>();
        services.AddScoped<NotificationTaskExecutor>();
        services.AddScoped<TriggerTransitionTaskExecutor>();

        // Trigger Transition Strategies
        services.AddScoped<ITriggerTransitionStrategyFactory, TriggerTransitionStrategyFactory>();
        services.AddScoped<ITriggerTransitionHttpTaskFactory, TriggerTransitionHttpTaskFactory>();
        services.AddScoped<StartTriggerStrategy>();
        services.AddScoped<DirectTriggerStrategy>();
        services.AddScoped<SubProcessTriggerStrategy>();
        services.AddScoped<GetInstanceDataTriggerStrategy>();
        
        // Scripting service
        services.AddScoped<IScriptContextFactory, ScriptContextFactory>();
        services.AddScoped<IScriptEngine, ScriptEngine>();
    }

    /// <summary>
    /// Configures workflow cast handlers.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    private static void AddCastHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IWorkflowCastHandler, FlowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, TaskWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, FunctionWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, ViewWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, SchemaWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, ExtensionWorkflowCastHandler>();
        services.AddSingleton<WorkflowCastProcessor>();
    }

    /// <summary>
    /// Adds the new transition pipeline architecture services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The service collection for method chaining.</returns>
    public static IServiceCollection AddTransitionPipeline(this IServiceCollection services)
    {
        // Context Factory
        services.AddScoped<ITransitionContextFactory, TransitionContextFactory>();
        services.AddScoped<IContextRefresher, ContextRefresher>();

        // Transition Data Mapping Service
        services.AddScoped<ITransitionDataMapper, TransitionDataMapper>();

        // Validation Services
        services.AddScoped<ITransitionValidationService, TransitionValidationService>();
        services.AddScoped<IInstanceDataValidationService, InstanceDataValidationService>();

        // Trigger Handlers
        services.AddScoped<ITransitionHandler, ManualTransitionHandler>();
        services.AddScoped<ITransitionHandler, AutomaticTransitionHandler>();
        services.AddScoped<ITransitionHandler, ScheduledTransitionHandler>();
        services.AddScoped<ITransitionHandler, EventTransitionHandler>();
        services.AddScoped<ITransitionHandlerFactory, TransitionHandlerFactory>();

        // Execution Strategies
        services.AddScoped<SyncTransitionStrategy>();
        services.AddScoped<AsyncTransitionStrategy>();
        services.AddScoped<IExecutionStrategyFactory, ExecutionStrategyFactory>();

        // Pipeline Steps (registered in execution order)
        services.AddScoped<ITransitionStep, ForwardToActiveSubflowStep>();
        services.AddScoped<ITransitionStep, CreateTransitionRecordStep>();
        services.AddScoped<ITransitionStep, RunOnExecuteTasksStep>();
        services.AddScoped<ITransitionStep, RunOnExitTasksStep>();
        services.AddScoped<ITransitionStep, ChangeStateStep>();
        services.AddScoped<ITransitionStep, RunOnEntryTasksStep>();
        services.AddScoped<ITransitionStep, HandleSubFlowStep>();
        services.AddScoped<ITransitionStep, ClearBusyOnResumeStep>();
        services.AddScoped<ITransitionStep, ScheduleTransitionsStep>();
        services.AddScoped<ITransitionStep, RunAutomaticTransitionsStep>();
        services.AddScoped<ITransitionStep, HandleFinishStep>();
        services.AddScoped<ITransitionStep, FinalizeTransitionStep>();
        services.AddScoped<ITransitionStep, ProcessInlineAutoChainStep>();

        // Pipeline
        services.AddScoped<TransitionPipeline>();

        // Re-entry System
        services.AddScoped<IReentryDispatcher, DefaultReentryDispatcher>();

        // Configure Re-entry Options
        services.Configure<ReentryOptions>(options =>
        {
            options.MaxAutoHops = 12;
            options.AllowInlineAuto = true;
            options.LockTimeout = TimeSpan.FromSeconds(30);
        });

        return services;
    }

    /// <summary>
    /// Configures the task factory based on configuration options.
    /// Selects between standard TaskFactory and PooledTaskFactory based on performance requirements.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    private static void ConfigureTaskFactory(IServiceCollection services)
    {
        // Configure TaskFactory options from configuration
        services.AddOptions<TaskFactoryOptions>()
            .BindConfiguration(TaskFactoryOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register TaskFactory implementation based on configuration
        services.AddScoped<ITaskFactory>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TaskFactoryOptions>>();
            var componentCacheStore = serviceProvider.GetRequiredService<IComponentCacheStore>();

            if (options.Value.UseObjectPooling)
            {
                // Get singleton PooledTaskFactory for object pooling efficiency
                return serviceProvider.GetRequiredService<PooledTaskFactory>();
            }

            var standardLogger = serviceProvider.GetRequiredService<ILogger<TaskFactory>>();
            return new TaskFactory(componentCacheStore, standardLogger);
        });

        // Register standard TaskFactory as scoped (stateless)
        services.AddScoped<TaskFactory>();

        // Register PooledTaskFactory as SINGLETON for efficient object pooling
        services.AddSingleton<PooledTaskFactory>();
    }
}