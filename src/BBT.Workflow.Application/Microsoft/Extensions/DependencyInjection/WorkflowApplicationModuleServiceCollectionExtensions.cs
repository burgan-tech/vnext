using BBT.Workflow.Application.Resilience;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitoring;
using BBT.Workflow.RepresentationEtag;
using BBT.Workflow.Resilience;
using BBT.Workflow.Runtime;
using BBT.Workflow.Extentions;
using BBT.Workflow.SubFlow;
using BBT.Workflow.Authorization;
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
    /// Adds the application module services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddApplicationModule(this IServiceCollection services)
    {
        services.AddAetherApplication();
        
        // Add transition pipeline architecture
        services.AddPipelineServices();

        // Configure service groups
        services.AddApplicationServices();
        services.AddCacheServices();
        services.AddTaskHandlers();
        services.AddComponentCacheHandlers();
        services.AddComponentValidators();
        
        return services;
    }
    
    /// <summary>
    /// Configures core application services.
    /// </summary>
    private static void AddApplicationServices(this IServiceCollection services)
    {
        // Application Services
        services.AddScoped<IDefinitionAppService, DefinitionAppService>();
        services.AddScoped<IInstanceCommandAppService, InstanceCommandAppService>();
        services.AddScoped<IInstanceQueryAppService, InstanceQueryAppService>();
        services.AddScoped<IViewContentResolutionService, ViewContentResolutionService>();
        services.AddScoped<IInstanceRetryAppService, InstanceRetryAppService>();
        services.AddScoped<IFunctionAppService, FunctionAppService>();
        services.AddScoped<ITransitionAuthorizationManager, TransitionAuthorizationManager>();
        services.AddScoped<IAuthorizeAppService, AuthorizeAppService>();
        services.AddScoped<IRepresentationEtagService, RepresentationEtagService>();
        services.AddScoped<IInstanceExtensionService, InstanceExtensionService>();
        services.AddScoped<ISubflowCompletionService, SubflowCompletionService>();
        services.AddScoped<ISubflowStateService, SubflowStateService>();
        services.AddScoped<ISubflowStarter, SubflowStarter>();
        services.AddScoped<ISubflowForwardingService, SubflowForwardingService>();
        services.AddScoped<IChildSubflowCancellationService, ChildSubflowCancellationService>();
        
        // Instance Services
        services.AddScoped<IInstanceCancellationService, InstanceCancellationService>();
        
        // Runtime Services
        services.AddScoped<IRuntimeService, RuntimeService>();
        services.AddScoped<IRuntimeCacheInitializer, RuntimeCacheInitializer>();
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
