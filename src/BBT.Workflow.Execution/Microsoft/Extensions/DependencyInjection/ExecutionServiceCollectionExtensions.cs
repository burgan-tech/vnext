using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Execution services.
/// </summary>
public static class ExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Adds the task invoker registry and all built-in invokers to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration for binding options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTaskInvokers(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        // Register DirectTrigger retry options
        if (configuration != null)
        {
            services.Configure<TriggerRetryOptions>(
                configuration.GetSection(TriggerRetryOptions.SectionName));
        }
        else
        {
            // Register default options if no configuration provided
            services.Configure<TriggerRetryOptions>(_ => { });
        }

        // Register WorkflowHttpClient instances
        services.AddWorkflowHttpClient();

        // Register the registry
        services.AddSingleton<ITaskInvokerRegistry, TaskInvokerRegistry>();
        
        // Register null metrics as default (can be overridden)
        services.TryAddSingleton<ITaskMetrics>(NullTaskMetrics.Instance);
        
        // Register all built-in remote execution invokers
        services.AddSingleton<ITaskInvoker, HttpTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprServiceTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprBindingTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprHttpEndpointTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprPubSubTaskInvoker>();
        services.AddSingleton<ITaskInvoker, NotificationTaskInvoker>();
        
        // Register trigger task remote invokers (for cross-domain execution)
        services.AddSingleton<ITaskInvoker, StartTriggerRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, DirectTriggerRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, SubProcessRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, GetInstanceDataRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, GetInstancesRemoteInvoker>();
        
        return services;
    }
    
    /// <summary>
    /// Adds custom task metrics implementation.
    /// </summary>
    /// <typeparam name="TMetrics">The metrics implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTaskMetrics<TMetrics>(this IServiceCollection services)
        where TMetrics : class, ITaskMetrics
    {
        services.AddSingleton<ITaskMetrics, TMetrics>();
        return services;
    }

    /// <summary>
    /// Registers named HttpClient instances for workflow task execution.
    /// - WorkflowHttpClient: Default with SSL validation
    /// - WorkflowHttpClient.NoSslValidation: Without SSL validation
    /// </summary>
    private static IServiceCollection AddWorkflowHttpClient(this IServiceCollection services)
    {
        // Default HTTP client with SSL validation enabled
        services.AddHttpClient(WorkflowHttpClientNames.Default, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                MaxConnectionsPerServer = 10,
                UseCookies = false
            });

        // HTTP client with SSL validation disabled
        services.AddHttpClient(WorkflowHttpClientNames.NoSslValidation, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                MaxConnectionsPerServer = 10,
                UseCookies = false,
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            });

        return services;
    }
}
