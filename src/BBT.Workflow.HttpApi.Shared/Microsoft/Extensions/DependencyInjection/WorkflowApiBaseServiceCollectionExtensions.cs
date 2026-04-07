using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Backward-compatible aliases that delegate to the new modular extension methods.
/// New code should use the specific module extensions directly
/// (e.g. <see cref="WorkflowAspNetCoreExtensions.AddWorkflowAspNetCore"/>).
/// </summary>
public static class WorkflowApiBaseServiceCollectionExtensions
{
    /// <inheritdoc cref="WorkflowAspNetCoreExtensions.AddWorkflowJsonSerializer"/>
    public static IServiceCollection AddJsonSerializerOptions(this IServiceCollection services)
        => services.AddWorkflowJsonSerializer();

    /// <inheritdoc cref="WorkflowDaprExtensions.AddWorkflowDapr"/>
    public static IServiceCollection AddDaprClients(this IServiceCollection services)
        => services.AddWorkflowDapr();

    /// <inheritdoc cref="WorkflowAspNetCoreExtensions.AddWorkflowAspNetCore"/>
    public static IServiceCollection AddAspNetCoreModules(this IServiceCollection services, IConfiguration configuration)
        => services.AddWorkflowAspNetCore(configuration);

    /// <inheritdoc cref="WorkflowDbContextExtensions.AddWorkflowDbContext"/>
    public static IServiceCollection AddDbContext(this IServiceCollection services, IConfiguration configuration)
        => services.AddWorkflowDbContext(configuration);

    /// <inheritdoc cref="WorkflowEventBusExtensions.AddWorkflowDomainEvents"/>
    public static IServiceCollection AddDomainEventsInfrastructure(this IServiceCollection services)
        => services.AddWorkflowDomainEvents();

    /// <inheritdoc cref="WorkflowBackgroundJobExtensions.AddWorkflowBackgroundJobs"/>
    public static IServiceCollection AddBackgroundJob(this IServiceCollection services)
        => services.AddWorkflowBackgroundJobs();

    /// <inheritdoc cref="WorkflowMapperExtensions.AddWorkflowMapper"/>
    public static IServiceCollection AppMapper(this IServiceCollection services)
        => services.AddWorkflowMapper();

    /// <inheritdoc cref="WorkflowTelemetryExtensions.AddWorkflowTelemetry"/>
    public static IServiceCollection AddTelemetry(this IServiceCollection services, IConfiguration configuration)
        => services.AddWorkflowTelemetry(configuration);

    /// <inheritdoc cref="WorkflowCachingExtensions.AddWorkflowDistributedCache"/>
    public static IServiceCollection AddDistributedCache(this IServiceCollection services, IConfiguration configuration)
        => services.AddWorkflowDistributedCache(configuration);

    /// <inheritdoc cref="WorkflowCachingExtensions.AddWorkflowDistributedLock"/>
    public static IServiceCollection AddDistributedLock(this IServiceCollection services, IConfiguration configuration)
        => services.AddWorkflowDistributedLock(configuration);

    /// <inheritdoc cref="WorkflowEventBusExtensions.AddWorkflowEventBus"/>
    public static IServiceCollection AddEventBus(this IServiceCollection services, IConfiguration configuration)
        => services.AddWorkflowEventBus(configuration);

    /// <inheritdoc cref="WorkflowExceptionHandlingExtensions.AddWorkflowExceptionHandling"/>
    public static IServiceCollection AddExceptionHandling(this IServiceCollection services)
        => services.AddWorkflowExceptionHandling();

    /// <inheritdoc cref="WorkflowRuntimeMiddlewareExtensions.AddWorkflowRuntimeMiddleware"/>
    public static IServiceCollection AddRuntimeMiddleware(this IServiceCollection services)
        => services.AddWorkflowRuntimeMiddleware();

    /// <inheritdoc cref="WorkflowRuntimeMiddlewareExtensions.AddWorkflowHeaderService"/>
    public static IServiceCollection AddHeaderService(this IServiceCollection services)
        => services.AddWorkflowHeaderService();
}
