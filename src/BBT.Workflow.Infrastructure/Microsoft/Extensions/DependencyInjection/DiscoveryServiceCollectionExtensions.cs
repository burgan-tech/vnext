using BBT.Workflow.Discovery;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Cross-field cache invariants (L1Ttl < RefreshInterval < L2Ttl, and friends). Every one of
        // them fails SILENTLY when broken - the cache either stops serving or widens its staleness
        // window, with no exception and no error log - so startup is the only place they are visible.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ServiceDiscoveryOptions>, ServiceDiscoveryOptionsValidator>());

        var options = optionsSection.Get<ServiceDiscoveryOptions>() ?? new ServiceDiscoveryOptions();

        // Outbound trace-id stamping for every discovery call (registration + resolution).
        services.AddTransient<DiscoveryTraceHeaderHandler>();

        // Register HttpClient with Polly policies
        services.AddDiscoveryHttpClient(DomainRegistrationService.HttpClientName, options);

        // The bulk read gets its OWN client, with identical settings but a SEPARATE circuit breaker.
        // Sharing one would let a bulk refresh that fails on every tick trip the breaker guarding the
        // per-domain lookup - the path a cache miss falls back to - so the refresh would break the
        // very thing it degrades into.
        services.AddDiscoveryHttpClient(DiscoveryRegistryClient.BulkHttpClientName, options);

        services.AddScoped<IDomainRegistrationService, DomainRegistrationService>();

        // The dapr provider caches positive results; harmless when it is not selected.
        services.AddMemoryCache();

        // Provider selection. Singleton, matching the previous resolver's lifetime.
        //
        // An unrecognized value falls back to http rather than throwing: a typo in an
        // environment variable must not move production traffic onto a new transport, and it
        // must not stop the host from starting either. The chosen provider is logged so the
        // decision is visible at boot.
        var provider = options.Provider?.Trim() ?? string.Empty;
        var isDaprProvider = provider.Equals(DiscoveryProviders.Dapr, StringComparison.OrdinalIgnoreCase);

        // The registry-read cache is scoped to the DEFAULT provider only.
        //
        // Under "dapr" the app-id comes from a naming convention and the common path makes no
        // network call at all, so there is nothing here for it to win - while its own short-lived
        // in-process app-id cache would become a second TTL stacked on this one, and a staleness
        // window built from two multiplying TTLs is one nobody can reason about mid-incident.
        //
        // Both conditions are decided HERE rather than inside the decorator, deliberately. A flag
        // checked at read time would leave entries written before the flip still readable after it,
        // so "Cache:Enabled=false" would not restore the previous behaviour - only approximate it.
        var cacheEnabled = !isDaprProvider && options.Cache.Enabled;

        services.AddSingleton<DiscoveryRegistryClient>();

        if (cacheEnabled)
        {
            services.AddSingleton<IDiscoveryL1Cache, DiscoveryL1Cache>();
            services.AddSingleton<CachingDiscoveryRegistryClient>();
            services.AddSingleton<IDiscoveryRegistryClient>(
                sp => sp.GetRequiredService<CachingDiscoveryRegistryClient>());
            services.AddSingleton<IDiscoveryCacheWriter>(
                sp => sp.GetRequiredService<CachingDiscoveryRegistryClient>());
            services.AddSingleton<IDiscoveryCacheRefresher, DiscoveryCacheRefresher>();
        }
        else
        {
            services.AddSingleton<IDiscoveryRegistryClient>(
                sp => sp.GetRequiredService<DiscoveryRegistryClient>());
        }

        if (isDaprProvider)
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
    /// Registers a named discovery <see cref="HttpClient"/> with the shared settings and the
    /// timeout -> retry -> circuit-breaker policy chain.
    /// </summary>
    /// <remarks>
    /// Policies are registered outermost-first. <c>client.Timeout</c> additionally bounds the whole
    /// <c>SendAsync</c>, so it caps the ENTIRE retry sequence rather than one attempt - raising
    /// <see cref="ServiceDiscoveryOptions.MaxRetryAttempts"/> without raising
    /// <see cref="ServiceDiscoveryOptions.TimeoutSeconds"/> buys nothing, because the extra attempts
    /// cannot fit inside the outer timeout.
    /// <para>
    /// Each named client gets its own policy instances, and therefore its own circuit-breaker state.
    /// That separation is the point of calling this twice.
    /// </para>
    /// </remarks>
    private static IServiceCollection AddDiscoveryHttpClient(
        this IServiceCollection services,
        string clientName,
        ServiceDiscoveryOptions options)
    {
        services.AddHttpClient(clientName, (sp, client) =>
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
            .AddPolicyHandler(GetTimeoutPolicy(options))
            .AddPolicyHandler(GetRetryPolicy(options))
            .AddPolicyHandler(GetCircuitBreakerPolicy(options));

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