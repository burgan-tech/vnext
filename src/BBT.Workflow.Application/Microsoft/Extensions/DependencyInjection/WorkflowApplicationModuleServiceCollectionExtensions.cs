using BBT.Workflow.Application.Resilience;
using BBT.Workflow.Caching;
using BBT.Workflow.Components;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Caching;
using BBT.Workflow.Monitoring;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Extentions;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Authorization;
using BBT.Workflow.BackgroundJobs;
using BBT.Workflow.Events;
using BBT.Workflow.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for setting up application services in an <see cref="IServiceCollection" />.
/// </summary>
public static class WorkflowApplicationModuleServiceCollectionExtensions
{
    /// <summary>
    /// Adds the full application module: pipeline, app services, cache, task handlers, cast handlers, and validators.
    /// Used by hosts that run the workflow execution pipeline (Orchestration, Inbox, Outbox).
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddApplicationModule(this IServiceCollection services)
    {
        services.AddAetherApplication();

        services.AddPipelineServices();
        services.AddApplicationServices();
        services.AddApplicationCacheModule();
        services.AddTaskHandlers();
        services.AddComponentCacheHandlers();
        services.AddComponentValidators();

        return services;
    }

    /// <summary>
    /// Adds only the component cache infrastructure (cache store, cache backends, domain cache context)
    /// and component cast handlers/validators -- without the transition pipeline or app services.
    /// Use this for read-only hosts (e.g. Monitor) that need <see cref="IComponentCacheStore"/>
    /// but do not execute workflow transitions.
    /// </summary>
    public static IServiceCollection AddApplicationCacheModule(this IServiceCollection services)
    {
        services.AddCacheServices();
        services.AddComponentCacheHandlers();
        services.AddComponentValidators();
        return services;
    }

    /// <summary>
    /// Configures core application services (app services, instance services, runtime services).
    /// </summary>
    private static void AddApplicationServices(this IServiceCollection services)
    {
           services.AddOptions<InstanceFilteringOptions>()
            .BindConfiguration(InstanceFilteringOptions.SectionName);
        services.AddOptions<StateFunctionCacheOptions>()
            .BindConfiguration(StateFunctionCacheOptions.SectionName);
        services.AddScoped<IStateFunctionCache, StateFunctionCache>();
        services.AddOptions<InstanceFunctionCacheOptions>()
            .BindConfiguration(InstanceFunctionCacheOptions.SectionName);
        services.AddScoped<IDataFunctionCache, DataFunctionCache>();
        // Application Services
        services.AddScoped<IDefinitionAppService, DefinitionAppService>();
        services.AddScoped<IInstanceCommandAppService, InstanceCommandAppService>();
        services.AddScoped<IInstanceQueryAppService, InstanceQueryAppService>();
        services.AddScoped<IViewContentResolutionService, ViewContentResolutionService>();
        services.AddScoped<IInstanceRetryAppService, InstanceRetryAppService>();
        services.AddScoped<IStateStoreCacheGateway, StateStoreCacheGateway>();
        services.AddScoped<IFunctionAppService, FunctionAppService>();
        services.AddScoped<IEventAppService, EventAppService>();
        services.AddScoped<IInstanceSelectorResolver, InstanceSelectorResolver>();
        services.AddScoped<IComponentDiscoveryAppService, ComponentDiscoveryAppService>();
        services.AddScoped<ITransitionAuthorizationManager, TransitionAuthorizationManager>();
        services.AddScoped<IAuthorizeAppService, AuthorizeAppService>();
        services.AddScoped<IRepresentationEtagService, RepresentationEtagService>();
        services.AddScoped<ISchemaFieldFilterService, SchemaFieldFilterService>();
        services.AddScoped<IInstanceExtensionService, InstanceExtensionService>();
        services.AddScoped<IWorkflowOutputMappingService, WorkflowOutputMappingService>();
        services.AddScoped<ISubflowOutputMappingService, SubflowOutputMappingService>();
        services.AddScoped<ISubflowCompletionService, SubflowCompletionService>();
        services.AddScoped<ISubflowCancellationService, SubflowCancellationService>();
        services.AddScoped<ISubflowFaultService, SubflowFaultService>();
        services.AddScoped<ISubflowStateService, SubflowStateService>();
        services.AddScoped<ISubflowStarter, SubflowStarter>();
        services.AddScoped<ISubflowForwardingService, SubflowForwardingService>();
        services.AddScoped<IInstanceBusyManager, InstanceBusyManager>();
        services.AddScoped<IChildSubflowCancellationService, ChildSubflowCancellationService>();
        services.AddScoped<IChildSubflowFaultService, ChildSubflowFaultService>();
        services.AddScoped<ITransitionJobEnqueuer, TransitionJobEnqueuer>();
        services.AddScoped<IStateNotificationScheduler, StateNotificationScheduler>();

        // Instance Services
        services.AddScoped<IInstanceCancellationService, InstanceCancellationService>();
        
        // Runtime Services
        services.AddScoped<IRuntimeService, RuntimeService>();
    }

    /// <summary>
    /// Configures component cache store with metrics.
    /// </summary>
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
        
        // Cache Backend Services
        services.AddSingleton<ICacheBackend<Workflow>, RuntimeCacheBackend<Workflow>>();
        services.AddSingleton<ICacheBackend<WorkflowTask>, RuntimeCacheBackend<WorkflowTask>>();
        services.AddSingleton<ICacheBackend<SchemaDefinition>, RuntimeCacheBackend<SchemaDefinition>>();
        services.AddSingleton<ICacheBackend<Function>, RuntimeCacheBackend<Function>>();
        services.AddSingleton<ICacheBackend<View>, RuntimeCacheBackend<View>>();
        services.AddSingleton<ICacheBackend<Extension>, RuntimeCacheBackend<Extension>>();
        services.AddSingleton<ICacheBackend<Mapping>, RuntimeCacheBackend<Mapping>>();

        // Domain Cache Context
        services.AddSingleton<DomainCacheContext>();
        services.AddSingleton<IDomainCacheContext>(serviceProvider => serviceProvider.GetRequiredService<DomainCacheContext>());
    }

    /// <summary>
    /// Configures workflow cast handlers.
    /// </summary>
    private static void AddComponentCacheHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IWorkflowCastHandler, FlowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, TaskWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, FunctionWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, ViewWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, SchemaWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, ExtensionWorkflowCastHandler>();
        services.AddSingleton<IWorkflowCastHandler, MappingWorkflowCastHandler>();
        services.AddSingleton<WorkflowCastProcessor>();
    }

    /// <summary>
    /// Configures component validators for all component types.
    /// </summary>
    private static void AddComponentValidators(this IServiceCollection services)
    {
        services.AddSingleton<IComponentValidator, FlowComponentValidator>();
        services.AddSingleton<IComponentValidator, TaskComponentValidator>();
        services.AddSingleton<IComponentValidator, ViewComponentValidator>();
        services.AddSingleton<IComponentValidator, FunctionComponentValidator>();
        services.AddSingleton<IComponentValidator, SchemaComponentValidator>();
        services.AddSingleton<IComponentValidator, ExtensionComponentValidator>();
        services.AddSingleton<IComponentValidator, MappingComponentValidator>();
        services.AddSingleton<ComponentValidatorProcessor>();
    }

    /// <summary>
    /// Adds Result-based resilience pipeline services for retry logic.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Optional configuration for retry options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddResultResilience(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration != null)
        {
            services.Configure<ResultRetryOptions>(
                configuration.GetSection(ResultRetryOptions.SectionName));
        }
        else
        {
            services.Configure<ResultRetryOptions>(_ => { });
        }

        services.AddSingleton<IResultResiliencePipelineFactory, ResultResiliencePipelineFactory>();

        return services;
    }
}
