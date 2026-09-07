using System.Net;
using BBT.Workflow.Authorization.Remote;
using BBT.Workflow.Gateway;
using BBT.Workflow.Infrastructure.Instances.Remote;
using BBT.Workflow.Instances.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // The retry split falls exactly on typed-client boundaries — see RemoteServiceProfile.
        services.AddRemoteService<IRemoteInstanceCommandAppService, RemoteInstanceCommandAppService>(
            options, RemoteServiceProfile.Mutating);
        services.AddRemoteService<IRemoteInstanceRetryAppService, RemoteInstanceRetryAppService>(
            options, RemoteServiceProfile.Mutating);
        services.AddRemoteService<IRemoteInstanceQueryAppService, RemoteInstanceQueryAppService>(
            options, RemoteServiceProfile.Read);
        services.AddRemoteService<IRemoteAuthorizeAppService, RemoteAuthorizeAppService>(
            options, RemoteServiceProfile.Read);
        services.AddRemoteService<RemoteRelatedInstanceReader, RemoteRelatedInstanceReader>(
            options, RemoteServiceProfile.Read);

        return services;
    }

    /// <summary>
    /// Registers a remote vnext service behind an <see cref="IRemoteTransport{TClient}"/> shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things are registered per client, and the split between them IS the design:
    /// <list type="number">
    /// <item>A <b>named</b> <see cref="HttpClient"/> (<c>typeof(TClient).Name</c>) carrying the
    /// resilience pipeline — timeout, profile-gated retry, circuit breaker, decompression,
    /// <c>X-Internal-Operation</c>. This is the HTTP shell's wire and the <c>http</c> provider's
    /// path; it is unchanged from the pre-Dapr registration.</item>
    /// <item>The transport shells: <see cref="HttpRemoteTransport{TClient}"/> plus a
    /// <see cref="RemoteTransportRouter{TClient}"/> exposed as <see cref="IRemoteTransport{TClient}"/>.
    /// The Dapr shell, when present, is registered separately and found by the router lazily.</item>
    /// <item><typeparamref name="TImplementation"/> itself, transient, taking the shell — never an
    /// <see cref="HttpClient"/> — so it is oblivious to which wire a given call travels.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Named rather than typed <c>AddHttpClient</c> on purpose: the typed form constructs
    /// <typeparamref name="TImplementation"/> with an <see cref="HttpClient"/> argument through
    /// <c>ActivatorUtilities</c>, and the implementation no longer accepts one.
    /// </para>
    /// </remarks>
    /// <typeparam name="TClient">The interface type (e.g. IRemoteInstanceCommandAppService).</typeparam>
    /// <typeparam name="TImplementation">The implementation type that uses <see cref="IRemoteTransport{TClient}"/>.</typeparam>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Remote options used for timeout and policy configuration.</param>
    /// <param name="profile">
    /// Whether the client's endpoints may be retried at the transport layer. Defaults to
    /// <see cref="RemoteServiceProfile.Read"/> so a registration that omits it keeps the pre-split
    /// behaviour rather than silently dropping retry.
    /// </param>
    /// <returns>The HttpClient builder for the HTTP shell's named client, for optional further configuration.</returns>
    public static IHttpClientBuilder AddRemoteService<TClient, TImplementation>(
        this IServiceCollection services,
        RemoteOptions options,
        RemoteServiceProfile profile = RemoteServiceProfile.Read)
        where TClient : class
        where TImplementation : class, TClient
    {
        var builder = services
            .AddHttpClient(HttpRemoteTransport<TClient>.HttpClientName, ConfigureRemoteHttpClient)
            .ConfigurePrimaryHttpMessageHandler(() => CreatePrimaryHttpMessageHandler(options))
            .AddPolicyHandler(RemotePolicyFactory.Timeout(options));

        // Retry ONLY for read clients.
        //
        // A mutating cross-domain call (instances/start, internal/subflow-forward,
        // transitions/{key}, sub/*, busy, child-cancel, complete, longpoll/ack) is side-effecting
        // and must be attempted exactly once by the transport: a duplicate start or a duplicate
        // subflow-forward is data corruption, not a slow call. A transport failure surfaces as
        // Error.Transient("remote_network_error", …) and the user-defined error boundary decides
        // whether to retry — the only layer that knows whether the operation is safe to repeat.
        //
        // This cannot be expressed one layer down in Dapr: a Resiliency target is an app-id, a
        // domain's reads and mutations share one app-id, and retry.matching filters by HTTP
        // status code, not by path or method. Hence the split lives here — and in
        // RemotePolicyFactory.Compose for the Dapr shell, which applies the SAME policies
        // programmatically. Same conclusion the orchestration->execution policy reached; see
        // resiliency-orchestration.yaml in vnext-helm-charts.
        if (RemotePolicyFactory.AllowsRetry(options, profile))
        {
            builder.AddPolicyHandler(RemotePolicyFactory.Retry(options));
        }

        builder.AddPolicyHandler(RemotePolicyFactory.CircuitBreaker(options));

        // Explicit factory rather than type registration: AddHttpClient also registers a transient
        // HttpClient, which would make the shell's (IHttpClientFactory) and (HttpClient) constructors
        // equally resolvable and the container refuse to pick one.
        services.TryAddSingleton(sp =>
            new HttpRemoteTransport<TClient>(sp.GetRequiredService<IHttpClientFactory>()));
        // Dapr shell over the SDK's non-obsolete invocation client. In SDK 1.17 the whole
        // DaprClient.InvokeMethod* family is [Obsolete]; CreateInvokeHttpClient() is the surface its
        // message points at — an HttpClient whose InvocationHandler rewrites http://{appId}/... to the
        // sidecar and resolves DAPR_HTTP_ENDPOINT / DAPR_HTTP_PORT / DAPR_API_TOKEN itself. Same
        // policies as the HTTP pipeline above, applied programmatically. The router resolves this only
        // when an endpoint of Kind Dapr actually arrives, so hosts on the http provider never build it.
        services.TryAddSingleton<IDaprRemoteTransport<TClient>>(_ =>
            new DaprRemoteTransport<TClient>(
                Dapr.Client.DaprClient.CreateInvokeHttpClient(),
                RemotePolicyFactory.Compose(options, profile)));
        services.TryAddSingleton<IRemoteTransport<TClient>, RemoteTransportRouter<TClient>>();

        // Factory, not AddTransient<TClient, TImplementation>(): the implementation depends on
        // IDomainDiscoveryResolver, which only AddDomainDiscovery (AddInfrastructureRuntimeServices)
        // registers. Minimal hosts — DbMigrator — register the gateways through AddInfrastructureModule
        // without ever calling it, and never resolve a remote service at runtime. With a type
        // registration ValidateOnBuild inspects the constructor and aborts those hosts at startup
        // ("Unable to resolve service for type IDomainDiscoveryResolver"); the pre-Dapr typed
        // AddHttpClient<TClient, TImplementation> was a factory too, so this keeps that contract:
        // a missing resolver fails at first use, in a host that actually makes cross-domain calls.
        services.AddTransient<TClient>(sp => ActivatorUtilities.CreateInstance<TImplementation>(sp));

        return builder;
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
}
