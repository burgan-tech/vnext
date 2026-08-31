using System.Net;
using BBT.Aether.Users;
using BBT.Workflow.Authorization.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Timeout;

namespace BBT.Workflow.Authorization.Extensions;

/// <summary>
/// Registers the configured <see cref="ICallerRoleResolver"/>. The provider is chosen once, at
/// startup, from the <c>CallerRoleProvider</c> configuration section — never per request.
/// </summary>
public static class CallerRoleResolverServiceExtensions
{
    /// <summary>Name of the named HttpClient the morph-idm resolver is built on.</summary>
    private const string MorphIdmClientName = "morph-idm";

    /// <summary>
    /// Binds <see cref="CallerRoleProviderOptions"/> and registers the matching resolver. Anything
    /// other than <c>morph-idm</c> — including a missing section or an unrecognized name — resolves to
    /// the default provider, so a configuration mistake degrades to the runtime's original behaviour
    /// rather than taking the host down at startup.
    /// </summary>
    public static IServiceCollection AddCallerRoleResolver(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var section = configuration.GetSection(CallerRoleProviderOptions.SectionName);
        services.Configure<CallerRoleProviderOptions>(section);

        var options = section.Get<CallerRoleProviderOptions>() ?? new CallerRoleProviderOptions();

        if (!string.Equals(options.Provider, CallerRoleProviderOptions.MorphIdmProvider,
                StringComparison.OrdinalIgnoreCase))
        {
            services.AddScoped<ICallerRoleResolver, DefaultCallerRoleResolver>();
            return services;
        }

        var morphIdm = options.MorphIdm;

        // A named client plus an explicit scoped registration, deliberately not the
        // AddHttpClient<TClient,TImpl> typed-client overload: that overload registers the client as
        // TRANSIENT, so every injection site would get its own resolver and its own memo — one IDM
        // call per authorization surface instead of one per request. The scoped lifetime here is what
        // makes MorphIdmCallerRoleResolver's memoization mean what it claims.
        services
            .AddHttpClient(MorphIdmClientName, client =>
            {
                client.BaseAddress = new Uri(morphIdm.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(morphIdm.TimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                ServerCertificateCustomValidationCallback = morphIdm.ValidateSsl
                    ? null
                    : HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            })
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .Or<TimeoutRejectedException>()
                .WaitAndRetryAsync(
                    morphIdm.MaxRetryAttempts,
                    attempt => TimeSpan.FromMilliseconds(
                        morphIdm.RetryDelayMilliseconds * Math.Pow(2, attempt - 1))))
            .AddPolicyHandler(HttpPolicyExtensions
                .HandleTransientHttpError()
                .CircuitBreakerAsync(
                    morphIdm.CircuitBreakerFailureThreshold,
                    TimeSpan.FromSeconds(morphIdm.CircuitBreakerTimeoutSeconds)));

        services.AddScoped<ICallerRoleResolver>(sp => new MorphIdmCallerRoleResolver(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(MorphIdmClientName),
            sp.GetRequiredService<ICurrentUser>(),
            sp.GetRequiredService<IOptions<CallerRoleProviderOptions>>(),
            sp.GetRequiredService<ILogger<MorphIdmCallerRoleResolver>>()));

        return services;
    }
}
