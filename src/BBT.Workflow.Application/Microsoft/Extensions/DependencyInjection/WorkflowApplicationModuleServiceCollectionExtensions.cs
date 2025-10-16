using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.States;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Factory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using BBT.Workflow.Execution.Rules;
using BBT.Workflow.Execution.StateMachine;
using BBT.Workflow.Extentions;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Tasks.Persistence;
using BBT.Workflow.Tasks.Persistence.Strategies;
using TaskFactory = BBT.Workflow.Tasks.Factory.TaskFactory;
using BBT.Workflow.Functions;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Strategies;

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
        
        // Add HttpClient configuration for task executors (manual configuration)
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
            ServerCertificateCustomValidationCallback = (message, cert, chain, sslPolicyErrors) => true
        });

        // You can register your application service here.
        services.AddSingleton<DomainCacheContext>();
        services.AddScoped<IAdminAppService, AdminAppService>();
        services.AddScoped<IInstanceCommandAppService, InstanceCommandAppService>();
        services.AddScoped<IInstanceQueryAppService, InstanceQueryAppService>();
         services.AddScoped<IFunctionAppService, FunctionAppService>();
        services.AddScoped<ITaskCommandAppService, TaskCommandAppService>();
        services.AddScoped<IScriptContextFactory, ScriptContextFactory>();
        
        // Register core execution service
        services.AddScoped<IWorkflowExecutionService, WorkflowExecutionService>();
        
        // Register auto transition service
        services.AddScoped<IAutoTransitionService, AutoTransitionService>();
        
        // Register instance refresh strategy
        services.AddScoped<IInstanceRefreshStrategy, InstanceRefreshStrategy>();
        
        // Register strategy factory
        services.AddScoped<IExecutionStrategyFactory, ExecutionStrategyFactory>();
        
        // Register instance start strategies
        services.AddScoped<IInstanceStartStrategy, SyncInstanceStartStrategy>();
        services.AddScoped<IInstanceStartStrategy, AsyncInstanceStartStrategy>();
        
        // Register transition strategies
        services.AddScoped<ITransitionStrategy, SyncTransitionStrategy>();
        services.AddScoped<ITransitionStrategy, AsyncTransitionStrategy>();

        services.AddScoped<IRuntimeService, RuntimeService>();
        // Register the original ComponentCacheStore as a concrete service
        services.AddSingleton<ComponentCacheStore>();
        
        // Register the metrics-aware decorator as the primary IComponentCacheStore
        services.AddSingleton<IComponentCacheStore>(serviceProvider =>
        {
            var originalStore = serviceProvider.GetRequiredService<ComponentCacheStore>();
            var workflowMetrics = serviceProvider.GetRequiredService<IWorkflowMetrics>();
            var logger = serviceProvider.GetRequiredService<ILogger<MetricsAwareComponentCacheStore>>();
            
            return originalStore.WithMetrics(workflowMetrics, logger);
        });

        // Instance Services
        services.AddScoped<IInstanceExtensionService, InstanceExtensionService>();

        // Task Factory Services - Configuration Driven
        ConfigureTaskFactory(services);

        // Task Persistence Strategies
        services.AddScoped<ITaskPersistenceStrategy, StandardTaskPersistenceStrategy>();
        services.AddScoped<ITaskPersistenceStrategy, ExtensionTaskPersistenceStrategy>();
        services.AddScoped<ITaskPersistenceStrategyFactory, TaskPersistenceStrategyFactory>();

        // Core workflow execution services - registered with interfaces for better testability and isolation
        services.AddScoped<IStateMachineExecutor, StateMachineExecutor>();
        services.AddScoped<ITaskOrchestrationService, TaskOrchestrationService>();
        services.AddScoped<ISubFlowService, SubFlowService>();
        services.AddScoped<ISubFlowCompletionService, SubFlowCompletionService>();
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
        services.AddScoped<SignalRTaskExecutor>();

        // Scripting service
        services.AddScoped<IScriptEngine, ScriptEngine>();

        // Rule execution service
        services.AddScoped<IRuleExecutionService, RuleExecutionService>();
        services.AddScoped<ITimerExecutionService, TimerExecutionService>();

        // Cast handlers
        services.AddSingleton<IWorkflowCastHandler, FlowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, TaskWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, FunctionWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, ViewWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, SchemaWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, ExtensionWorkflowCastHandler>();
        services.AddSingleton<WorkflowCastProcessor>();

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