using BBT.Workflow.Discovery;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering service discovery services with resilient HttpClient.
/// </summary>
public static class DiscoveryServiceCollectionExtensions
{
    /// <summary>
    /// Adds service discovery services with resilient HttpClient configuration.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configureOptions">Optional action to configure service discovery options.</param>
    /// <returns>Service collection for chaining.</returns>
    public static IServiceCollection AddDomainDiscovery(
        this IServiceCollection services,
        Action<ServiceDiscoveryOptions>? configureOptions = null)
    {
        var configuration = services.GetConfiguration();

        // Configure options
        var optionsSection = configuration.GetSection(ServiceDiscoveryOptions.SectionName);

        services.Configure<ServiceDiscoveryOptions>(optionsSection);

        if (configureOptions != null)
        {
            services.Configure(configureOptions);
        }

        var options = optionsSection.Get<ServiceDiscoveryOptions>() ?? new ServiceDiscoveryOptions();

        // Outbound trace-id stamping for every discovery call (registration + resolution).
        services.AddTransient<DiscoveryTraceHeaderHandler>();

        // Register HttpClient with Polly policies
        services.AddHttpClient(DomainRegistrationService.HttpClientName, (sp, client) =>
            {
                var runtimeInfoProvider = sp.GetRequiredService<IRuntimeInfoProvider>();
                var clientOptions = sp.GetRequiredService<IOptions<ServiceDiscoveryOptions>>().Value;

                if (!string.IsNullOrWhiteSpace(clientOptions.BaseUrl))
                {
                    client.BaseAddress = new Uri(clientOptions.BaseUrl);
                }

                client.Timeout = TimeSpan.FromSeconds(clientOptions.TimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("User-Agent",
                    $"vnext-discovery/{runtimeInfoProvider.Version} ({runtimeInfoProvider.Domain})");

                // Add internal operation header for circuit breaker context
                if (clientOptions.EnableCircuitBreakerBypass)
                {
                    client.DefaultRequestHeaders.Add(clientOptions.InternalOperationHeader, "true");
                }
            })
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
                };

                // Disable SSL validation if configured (useful for development environments)
                if (!options.ValidateSsl)
                {
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                return handler;
            })
            .AddHttpMessageHandler<DiscoveryTraceHeaderHandler>()
            // Registered outermost-first: timeout wraps retry wraps circuit breaker. client.Timeout
            // (set above from clientOptions.TimeoutSeconds) additionally bounds the whole SendAsync,
            // so it caps the ENTIRE retry sequence, not one attempt. Raising MaxRetryAttempts without
            // also raising TimeoutSeconds buys nothing — the extra attempts simply can't fit inside
            // the outer timeout. See docs/superpowers/specs/2026-08-28-discovery-direct-and-load-gap-spec.md
            // ("Decisions taken" / TimeoutSeconds) for the arithmetic.
            .AddPolicyHandler(GetTimeoutPolicy(options))
            .AddPolicyHandler(GetRetryPolicy(options))
            .AddPolicyHandler(GetCircuitBreakerPolicy(options));

        services.AddScoped<IDomainRegistrationService, DomainRegistrationService>();

        // Registry reads are shared by both providers: the http provider needs the baseUrl, the
        // dapr provider only the optional appId override. Registration and health are untouched
        // by the Dapr migration — only address resolution moved.
        services.AddSingleton<IDiscoveryRegistryClient, DiscoveryRegistryClient>();

        // The dapr provider caches positive results; harmless when it is not selected.
        services.AddMemoryCache();

        // Provider selection. Singleton, matching the previous resolver's lifetime.
        //
        // An unrecognized value falls back to http rather than throwing: a typo in an
        // environment variable must not move production traffic onto a new transport, and it
        // must not stop the host from starting either. The chosen provider is logged so the
        // decision is visible at boot.
        var provider = options.Provider?.Trim() ?? string.Empty;

        if (provider.Equals(DiscoveryProviders.Dapr, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IDomainDiscoveryResolver, DaprDomainDiscoveryProvider>();
        }
        else
        {
            services.AddSingleton<IDomainDiscoveryResolver, HttpDomainDiscoveryProvider>();
        }

        return services;
    }

    /// <summary>
    /// Creates a retry policy with exponential backoff.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy(ServiceDiscoveryOptions options)
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
                        "Service Discovery retry attempt {RetryCount} for {OperationKey} after {Delay}ms. Reason: {Exception}",
                        retryCount,
                        context.OperationKey,
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? outcome.Result?.ReasonPhrase ?? "Unknown");
                });
    }

    /// <summary>
    /// Creates a circuit breaker policy with enhanced logging.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy(ServiceDiscoveryOptions options)
    {
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: options.CircuitBreakerFailureThreshold,
                durationOfBreak: TimeSpan.FromSeconds(options.CircuitBreakerTimeoutSeconds),
                onBreak: (exception, duration) =>
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Service Discovery circuit breaker opened. Duration: {duration.TotalMilliseconds}ms. " +
                        $"Failure threshold: {options.CircuitBreakerFailureThreshold}. Break duration: {options.CircuitBreakerTimeoutSeconds}s. " +
                        $"Exception: {exception.Exception?.Message ?? exception.Result?.ReasonPhrase ?? "Unknown"}");
                },
                onReset: () =>
                {
                    System.Diagnostics.Debug.WriteLine("Service Discovery circuit breaker reset.");
                },
                onHalfOpen: () =>
                {
                    System.Diagnostics.Debug.WriteLine("Service Discovery circuit breaker half-open.");
                });
    }

    /// <summary>
    /// Creates a timeout policy.
    /// </summary>
    private static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy(ServiceDiscoveryOptions options)
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            TimeoutStrategy.Pessimistic);
    }

    /// <summary>
    /// Extension to get logger from Polly context.
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