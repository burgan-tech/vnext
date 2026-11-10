using BBT.Workflow.Execution.Core.Http;
using BBT.Workflow.Execution;
using Dapr.AI.Conversation.Extensions;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Dapr.Client;

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
        services.AddWorkflowHttpClient(configuration);

        // Register the registry
        services.AddSingleton<ITaskInvokerRegistry, TaskInvokerRegistry>();
        
        // Register null metrics as default (can be overridden)
        services.TryAddSingleton<ITaskMetrics>(NullTaskMetrics.Instance);

        // Dapr Conversation (AI/LLM) client used by the DaprConversation invoker.
        services.AddDaprConversationClient();

        // Shared Dapr state-store gateway used by the StateStore invoker. Also
        // registered from the Application layer (TryAdd) when both layers live in one process.
        services.TryAddSingleton<BBT.Workflow.Execution.Core.StateStores.IStateStoreClient,
            BBT.Workflow.Execution.Core.StateStores.DaprStateStoreClient>();
        
        // The one service-invocation surface every Dapr-facing invoker sends through. Built on
        // DaprClient.CreateInvokeHttpClient() — the SDK's non-obsolete invocation client — since the
        // whole DaprClient.InvokeMethod* family is [Obsolete] in 1.17. Singleton: it targets the local
        // sidecar, so handler rotation for DNS refresh is not a concern. TryAdd because the
        // Application layer registers the same type for RemoteInvokerService.
        services.TryAddSingleton(new DaprServiceInvocationClient(DaprClient.CreateInvokeHttpClient()));

        // Register all built-in remote execution invokers
        services.AddSingleton<ITaskInvoker, HttpTaskInvoker>();
        services.AddSingleton<ITaskInvoker, SoapTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprServiceTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprBindingTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprHttpEndpointTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprPubSubTaskInvoker>();
        services.AddSingleton<ITaskInvoker, DaprConversationTaskInvoker>();
        services.AddSingleton<ITaskInvoker, StateStoreTaskInvoker>();
        
        // Register trigger task remote invokers (for cross-domain execution)
        services.AddSingleton<ITaskInvoker, StartTriggerRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, DirectTriggerRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, SubProcessRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, GetInstanceDataRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, GetInstancesRemoteInvoker>();
        services.AddSingleton<ITaskInvoker, GetInstanceRemoteInvoker>();
        
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
    /// Pool shape from <see cref="WorkflowHttpClientOptions"/> (section <c>WorkflowHttpClient</c>);
    /// the primary handler is the same <see cref="WorkflowHttpMessageHandlerFactory"/> build the
    /// Orchestration host uses, so a task sees one connection policy whichever host calls out.
    /// </summary>
    private static IServiceCollection AddWorkflowHttpClient(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        var httpClientOptions = services.AddOptions<WorkflowHttpClientOptions>();
        if (configuration is not null)
        {
            httpClientOptions.Bind(configuration.GetSection(WorkflowHttpClientOptions.SectionName));
        }
        httpClientOptions.ValidateDataAnnotations().ValidateOnStart();

        // Resolved lazily, the first time the factory builds the handler — options are bound by then.
        static WorkflowHttpClientPoolSettings ResolvePoolSettings(IServiceProvider provider) =>
            provider.GetRequiredService<IOptions<WorkflowHttpClientOptions>>().Value.ToPoolSettings();

        // Default HTTP client with SSL validation enabled. Infinite handler lifetime: connection
        // retirement is PooledConnectionLifetime's job, not a pool-wide drop every two minutes.
        services.AddHttpClient(WorkflowHttpClientNames.Default, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = int.MaxValue;
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .SetHandlerLifetime(WorkflowHttpMessageHandlerFactory.HandlerLifetime)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                WorkflowHttpMessageHandlerFactory.Create(ResolvePoolSettings(provider), validateSsl: true));

        // HTTP client with SSL validation disabled
        services.AddHttpClient(WorkflowHttpClientNames.NoSslValidation, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = int.MaxValue;
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .SetHandlerLifetime(WorkflowHttpMessageHandlerFactory.HandlerLifetime)
            .ConfigurePrimaryHttpMessageHandler(provider =>
                WorkflowHttpMessageHandlerFactory.Create(ResolvePoolSettings(provider), validateSsl: false));

        return services;
    }
}
