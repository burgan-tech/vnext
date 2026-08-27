using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.BackgroundJobs.Recovery;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Helpers;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.Scripting.Sandbox;
using Microsoft.Extensions.Configuration;
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

        // Error boundary services (consolidated in Execution/ErrorHandling)
        services.AddErrorBoundaryServices();

        // Task coordination and orchestration
        services.AddTaskCoordination();

        // Supporting services
        services.AddTaskFactories();
        services.AddTaskPersistence();
        services.AddScriptingServices();

        // Background job recovery and execution options
        services.AddBackgroundJobServices();

        return services;
    }

    private static IServiceCollection AddBackgroundJobServices(this IServiceCollection services)
    {
        services.AddOptions<WorkflowExecutionOptions>()
            .BindConfiguration(WorkflowExecutionOptions.SectionName);

        // Budget-hierarchy guard (invocation timeout ⊂ job budget ⊂ lock lease) — validated
        // on first options resolution so misconfiguration fails fast.
        services.AddSingleton<
            Microsoft.Extensions.Options.IValidateOptions<WorkflowExecutionOptions>,
            WorkflowExecutionOptionsValidator>();

        services.AddScoped<IJobTimeoutRecoveryService, JobTimeoutRecoveryService>();

        return services;
    }

    /// <summary>
    /// Adds the new Task Executor architecture services.
    /// Each task type has a dedicated executor that handles the full lifecycle.
    /// </summary>
    private static IServiceCollection AddTaskExecutors(this IServiceCollection services)
    {
        // FanOut global bulkhead (process-level ceiling across all fan-out batches). A
        // misconfigured MaxConcurrentItems <= 0 would deadlock every fan-out batch in the
        // process on its first item, so validate at startup instead of at first use.
        services.AddOptions<FanOutOptions>()
            .BindConfiguration(FanOutOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton<FanOutConcurrencyLimiter>();

        // Remote invoker service for Dapr invocation
        services.TryAddScoped<IRemoteInvokerService, RemoteInvokerService>();

        // Executor registry
        services.TryAddScoped<ITaskExecutorRegistry, TaskExecutorRegistry>();

        // Script executor (no remote - inline execution)
        services.AddTaskExecutor<ScriptTaskExecutor>();

        // Human task executor (no remote - state change only)
        services.AddTaskExecutor<HumanTaskExecutor>();

        // HTTP, SOAP and Dapr remote executors
        services.AddTaskExecutor<HttpTaskExecutor>();
        services.AddTaskExecutor<SoapTaskExecutor>();

        // External HTTP executor (issue #399): the orchestrator performs the user-defined URL call
        // in-process — no /execution/invoke hop. The named HTTP clients it sends through are
        // concrete transport and are registered by the Infrastructure module
        // (AddExternalHttpTaskClients), which every composition root pairs with this one.
        services.TryAddScoped<IExternalHttpTaskInvoker, ExternalHttpTaskInvoker>();
        services.AddTaskExecutor<ExternalHttpTaskExecutor>();
        services.AddTaskExecutor<DaprServiceTaskExecutor>();
        services.AddTaskExecutor<DaprBindingTaskExecutor>();
        services.AddTaskExecutor<DaprHttpEndpointTaskExecutor>();
        services.AddTaskExecutor<DaprPubSubTaskExecutor>();
        services.AddTaskExecutor<DaprConversationTaskExecutor>();
        services.AddTaskExecutor<StateStoreTaskExecutor>();

        // Cache-Aside (read-through) executor: cache get/set is dispatched to the Execution service via
        // the StateStore invoker; the source task on a miss is orchestrated locally.
        services.AddTaskExecutor<CacheAsideTaskExecutor>();

        // Notification task executor (multi-channel direct Dapr binding dispatch)
        services.TryAddScoped<IStateChannelMessageBuilder, StateChannelMessageBuilder>();
        services.TryAddScoped<IStateNotificationDispatcher, StateNotificationDispatcher>();
        services.AddTaskExecutor<NotificationTaskExecutor>();

        // Trigger task executors (domain-aware: local or remote)
        services.AddTaskExecutor<SubProcessTaskExecutor>();
        services.AddTaskExecutor<StartTriggerTaskExecutor>();
        services.AddTaskExecutor<DirectTriggerTaskExecutor>();
        services.AddTaskExecutor<GetInstanceDataTaskExecutor>();
        services.AddTaskExecutor<GetInstancesTaskExecutor>();
        services.AddTaskExecutor<GetInstanceTaskExecutor>();

        // FanOut executor: runs the referenced inner task once per resolved item, in parallel,
        // and joins the outcomes into a single output (one instance-data write per batch).
        services.AddTaskExecutor<FanOutTaskExecutor>();

        return services;
    }

    /// <summary>
    /// Adds unified task evaluators for special tasks (Condition, Timer).
    /// Evaluators implement ITaskEvaluator&lt;T&gt; for type-safe evaluation.
    /// </summary>
    private static IServiceCollection AddTaskEvaluators(this IServiceCollection services)
    {
        // Condition: Roslyn scripts + Dynamic Expresso (routed by ScriptCode.Location)
        services.AddScoped<ScriptConditionEvaluator>();
        services.AddScoped<DynamicExpressoConditionEvaluator>();
        services.AddScoped<IConditionEvaluator, RoutingConditionEvaluator>();
        services.AddScoped<ITimerEvaluator, ScriptTimerEvaluator>();

        // Dynamic Expresso string evaluator (e.g. CacheAside key expressions).
        services.AddScoped<IDynamicExpressoValueEvaluator, DynamicExpressoValueEvaluator>();

        // Unified evaluator registry
        services.AddScoped<ITaskEvaluatorRegistry, TaskEvaluatorRegistry>();

        return services;
    }

    /// <summary>
    /// Adds consolidated error boundary services from Execution/ErrorHandling.
    /// Provides hierarchical boundary resolution (Task -> State -> Global),
    /// action execution, and Polly retry integration.
    /// </summary>
    private static IServiceCollection AddErrorBoundaryServices(this IServiceCollection services)
    {
        // Error normalization
        services.TryAddScoped<IErrorNormalizer, ErrorNormalizer>();

        // Boundary resolution with hierarchical lookup
        services.TryAddScoped<IErrorBoundaryResolver, ErrorBoundaryResolver>();

        // Execution Error Factory
        services.TryAddScoped<IExecutionErrorFactory, ExecutionErrorFactory>();

        // Action execution (Abort, Retry, Rollback, Notify, Log, Ignore)
        services.TryAddScoped<IErrorActionExecutor, ErrorActionExecutor>();

        // Polly retry policy factory
        services.TryAddSingleton<IRetryPolicyFactory, PollyRetryPolicyFactory>();

        return services;
    }

    /// <summary>
    /// Adds task coordination services (ITaskCoordinator, ITaskConditionService, ITaskTimerService).
    /// Uses consolidated error boundary services and TaskExecutionEngine.
    /// </summary>
    private static IServiceCollection AddTaskCoordination(this IServiceCollection services)
    {
        // Task execution engine (single task lifecycle)
        services.TryAddScoped<ITaskExecutionEngine, TaskExecutionEngine>();

        // Coordinator services (orchestration only)
        services.AddScoped<ITaskCoordinator, TaskCoordinator>();
        services.AddScoped<ITaskCoordinatorExtended, TaskCoordinator>();
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
    /// ScriptServices is scoped to provide proper isolation per request.
    /// </summary>
    private static IServiceCollection AddScriptingServices(this IServiceCollection services)
    {
        // Scoped, not singleton: the factory forwards the scoped IRelatedInstanceReader and
        // IInstanceCorrelationRepository gateway services (registered in
        // GatewayServiceCollectionExtensions.AddInstanceGatewayServices) into every ScriptContextBuilder
        // it creates. A singleton factory would capture those scoped dependencies once from the root
        // scope and reuse them for the app's lifetime — a stale-DbContext captive-dependency bug. The
        // factory itself is stateless, so scoping it down has no other effect. Every current consumer of
        // IScriptContextFactory (pipeline steps, app services, job handlers registered via
        // AddAetherBackgroundJob) is itself registered scoped, so this does not turn a captive
        // dependency the other way around into a resolution failure — verified by grepping every
        // constructor-injection site of IScriptContextFactory across src/orchestration/execution/workers.
        services.AddScoped<IScriptContextFactory, ScriptContextFactory>();

        // Guardrails for related-instance access from mapping scripts (see RelatedAccessOptions),
        // bound defensively so a missing section keeps the built-in defaults.
        services.AddOptions<RelatedAccessOptions>()
            .BindConfiguration(RelatedAccessOptions.SectionName);

        // Default raw-body provider resolves the original request body from the ambient job scope only.
        // HTTP hosts replace this (via Replace) with one that also reads the live HTTP request.
        services.TryAddSingleton<IRequestRawBodyProvider, AmbientRequestRawBodyProvider>();

        // Sandbox + custom-script-helpers options, bound defensively from configuration so a missing
        // section simply yields safe defaults (sandbox disabled, helpers disabled). Registered as
        // concrete singletons (not IOptions) so consumers can be constructed without an IConfiguration.
        services.TryAddSingleton(sp => BindSection<ScriptSandboxOptions>(sp, ScriptSandboxOptions.SectionName));
        services.TryAddSingleton(sp => BindSection<ScriptHelpersOptions>(sp, ScriptHelpersOptions.SectionName));

        // Evaluator is stateless - singleton for efficiency (caches MetadataReferences only).
        // Built via factory so it receives the (singleton) sandbox options.
        services.TryAddSingleton<IEvaluator>(sp =>
            new CSharpEvaluator(sp.GetRequiredService<ScriptSandboxOptions>()));

        // Helper-set registry is a process-wide singleton (shared collectible ALCs, content-hash cache).
        services.TryAddSingleton<IScriptHelperRegistry>(sp =>
            new ScriptHelperRegistry(
                sp.GetRequiredService<IEvaluator>(),
                sp.GetRequiredService<ScriptSandboxOptions>()));

        // Secret bundle cache options — module-local, BindSection pattern like Sandbox/Helpers.
        services.TryAddSingleton(sp => BindSection<SecretCacheOptions>(sp, SecretCacheOptions.SectionName));
        // Defensive: normally registered by the application module already.
        services.TryAddSingleton(TimeProvider.System);
        // Singleton on purpose: ScriptServices is scoped, but the cache must outlive request scopes.
        // In-process on purpose: secret material stays off Redis (see ScriptSecretCache docs).
        services.TryAddSingleton<IScriptSecretCache, ScriptSecretCache>();

        // Script services - scoped for per-request isolation (requires DaprClient to be registered)
        services.TryAddScoped<IScriptServices, ScriptServices>();

        services.TryAddScoped<IScriptEngine, ScriptEngine>();

        return services;
    }

    /// <summary>
    /// Binds a configuration section into a fresh options instance, tolerating an absent
    /// <see cref="IConfiguration"/> or section (returns defaults) so consumers stay test-friendly.
    /// </summary>
    private static T BindSection<T>(IServiceProvider sp, string sectionName) where T : class, new()
    {
        var options = new T();
        var configuration = sp.GetService<IConfiguration>();
        configuration?.GetSection(sectionName)?.Bind(options);
        return options;
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
