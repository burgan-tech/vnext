using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.Metrics;
using BBT.Workflow.Execution.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering Execution services.
/// </summary>
public static class ExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Named HttpClient for trigger invoker HTTP requests.
    /// </summary>
    public const string TriggerInvokerHttpClientName = "TriggerInvoker";

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

        // Get options for configuring HttpClient policies
        var triggerOptions = configuration?
            .GetSection(TriggerRetryOptions.SectionName)
            .Get<TriggerRetryOptions>() ?? new TriggerRetryOptions();

        // Register named HttpClient for trigger invokers with Polly policies
        services.AddHttpClient(TriggerInvokerHttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(triggerOptions.TimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            })
            .AddPolicyHandler(GetTimeoutPolicy(triggerOptions))
            .AddPolicyHandler(GetRetryPolicy(triggerOptions))
            .AddPolicyHandler(GetCircuitBreakerPolicy(triggerOptions));

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
        
        return services;
    }

    /// <summary>
    /// Creates a retry policy with exponential backoff for trigger invokers
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(TriggerRetryOptions options)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                retryCount: options.MaxRetryAttempts,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                    options.RetryDelayMilliseconds * Math.Pow(2, retryAttempt - 1)));
    }

    /// <summary>
    /// Creates a circuit breaker policy for trigger invokers
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(TriggerRetryOptions options)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerFailureThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerTimeoutSeconds));
    }

    /// <summary>
    /// Creates a timeout policy for trigger invokers
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(TriggerRetryOptions options)
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            TimeoutStrategy.Pessimistic);
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
}
