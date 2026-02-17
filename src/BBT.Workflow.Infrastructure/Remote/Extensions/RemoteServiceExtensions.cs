using System.Net;
using BBT.Workflow.Authorization.Remote;
using BBT.Workflow.Infrastructure.Instances.Remote;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace BBT.Workflow.Remote.Extensions;

/// <summary>
/// Extension methods for registering remote vnext services with resilient HttpClient
/// </summary>
public static class RemoteServiceExtensions
{
    /// <summary>
    /// Adds remote vnext services with resilient HttpClient configuration
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureOptions">Optional action to configure remote instance options</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddVNextApiServices(
        this IServiceCollection services,
        Action<RemoteOptions>? configureOptions = null)
    {
        var configuration = services.GetConfiguration();
        // Configure options
        var optionsSection = configuration
            .GetSection(RemoteOptions.SectionName);

        services.Configure<RemoteOptions>(optionsSection);

        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        var options = optionsSection.Get<RemoteOptions>() ?? new RemoteOptions();

        services.AddRemoteService<IRemoteInstanceCommandAppService, RemoteInstanceCommandAppService>(options);
        services.AddRemoteService<IRemoteInstanceRetryAppService, RemoteInstanceRetryAppService>(options);
        services.AddRemoteService<IRemoteInstanceQueryAppService, RemoteInstanceQueryAppService>(options);
        services.AddRemoteService<IRemoteAuthorizeAppService, RemoteAuthorizeAppService>(options);

        return services;
    }

    /// <summary>
    /// Registers a remote vnext service with resilient HttpClient configuration (timeout, headers, handler, retry, circuit breaker).
    /// Use this to avoid duplicating HttpClient setup for each remote service.
    /// </summary>
    /// <typeparam name="TClient">The interface type (e.g. IRemoteInstanceCommandAppService).</typeparam>
    /// <typeparam name="TImplementation">The implementation type that uses HttpClient (e.g. RemoteInstanceCommandAppService).</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Remote options used for timeout and policy configuration.</param>
    /// <returns>The HttpClient builder for optional further configuration.</returns>
    public static IHttpClientBuilder AddRemoteService<TClient, TImplementation>(
        this IServiceCollection services,
        RemoteOptions options)
        where TClient : class
        where TImplementation : class, TClient
    {
        return services
            .AddHttpClient<TClient, TImplementation>(ConfigureRemoteHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => CreatePrimaryHttpMessageHandler(options))
            .AddPolicyHandler(GetTimeoutPolicy(options))
            .AddPolicyHandler(GetRetryPolicy(options))
            .AddPolicyHandler(GetCircuitBreakerPolicy(options));
    }

    /// <summary>
    /// Configures HttpClient with timeout, Accept/User-Agent headers, optional circuit-bypass header, and buffer limits.
    /// BaseAddress is resolved per request via IDomainDiscoveryResolver.
    /// </summary>
    private static void ConfigureRemoteHttpClient(IServiceProvider sp, HttpClient client)
    {
        var runtimeInfoProvider = sp.GetRequiredService<IRuntimeInfoProvider>();
        var clientOptions = sp.GetRequiredService<IOptions<RemoteOptions>>().Value;

        client.Timeout = TimeSpan.FromSeconds(clientOptions.TimeoutSeconds);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("User-Agent",
            $"vnext-runtime/{runtimeInfoProvider.Version} ({runtimeInfoProvider.Domain})");

        if (clientOptions.EnableCircuitBreakerBypass)
        {
            client.DefaultRequestHeaders.Add(clientOptions.InternalOperationHeader, "true");
        }

        client.MaxResponseContentBufferSize = int.MaxValue;
    }

    /// <summary>
    /// Creates the primary HTTP message handler with decompression, increased header/content buffer limits,
    /// and optional SSL validation (disabled when ValidateSsl is false, e.g. for development).
    /// </summary>
    private static HttpMessageHandler CreatePrimaryHttpMessageHandler(RemoteOptions options)
    {
        var handler = new HttpClientHandler()
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxResponseHeadersLength = 1024 * 1024, // 1MB
            MaxRequestContentBufferSize = int.MaxValue
        };

        if (!options.ValidateSsl)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }

    /// <summary>
    /// Creates a retry policy with exponential backoff
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(RemoteOptions options)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                retryCount: options.MaxRetryAttempts,
                sleepDurationProvider: retryAttempt => TimeSpan.FromMilliseconds(
                    options.RetryDelayMilliseconds * Math.Pow(2, retryAttempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    var logger = context.GetLogger();
                    logger?.LogWarning(
                        "Retry attempt {RetryCount} for {OperationKey} after {Delay}ms. Reason: {Exception}",
                        retryCount,
                        context.OperationKey,
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase ?? "Unknown");
                });
    }

    /// <summary>
    /// Creates a circuit breaker policy with enhanced logging and internal operation awareness
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(RemoteOptions options)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerFailureThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerTimeoutSeconds),
                onBreak: (exception, duration) =>
                {
                    // Enhanced logging for circuit breaker opening
                    // Note: Logging can be enhanced here if needed, but avoiding context access issue
                    System.Diagnostics.Debug.WriteLine(
                        $"Circuit breaker opened. Duration: {duration.TotalMilliseconds}ms. " +
                        $"Failure threshold: {options.CircuitBreakerFailureThreshold}. Break duration: {options.CircuitBreakerTimeoutSeconds}s. " +
                        $"Exception: {exception.Exception?.Message ?? exception.Result?.ReasonPhrase ?? "Unknown"}");
                },
                onReset: () =>
                {
                    // Log circuit breaker reset - could add logging here if needed
                },
                onHalfOpen: () =>
                {
                    // Log circuit breaker half-open state - could add logging here if needed
                });
    }

    /// <summary>
    /// Creates a timeout policy
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(RemoteOptions options)
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            TimeoutStrategy.Pessimistic);
    }

    /// <summary>
    /// Extension to get logger from Polly context
    /// </summary>
    private static ILogger? GetLogger(this Context context)
    {
        if (context.TryGetValue("logger", out var logger) && logger is ILogger log)
        {
            return log;
        }

        return null;
    }
}