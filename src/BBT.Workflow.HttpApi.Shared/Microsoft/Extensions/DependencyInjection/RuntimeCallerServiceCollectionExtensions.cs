using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the runtime-caller identity (vnext#1265).</summary>
public static class RuntimeCallerServiceCollectionExtensions
{
    /// <summary>
    /// Binds <c>Workflow:RuntimeCaller</c>, registers the host's signer/verifier and validates the key
    /// ring at startup. Call it on every host that sends or receives runtime-internal calls:
    /// Orchestration, Execution and Inbox.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <param name="runsFlowScripts">
    /// True on the host that compiles flow scripts (Orchestration): a key ring there requires the
    /// script sandbox, which keeps scripts from reading the key file.
    /// </param>
    public static IServiceCollection AddRuntimeCallerIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runsFlowScripts)
    {
        services.AddOptions<RuntimeCallerOptions>()
            .BindConfiguration(RuntimeCallerOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RuntimeCallerOptions>>(
            new RuntimeCallerOptionsValidator(configuration, runsFlowScripts)));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RuntimeCallerIdentity>();
        services.TryAddSingleton<IRuntimeCallerSigner>(sp => sp.GetRequiredService<RuntimeCallerIdentity>());
        services.TryAddSingleton<IRuntimeCallerVerifier>(sp => sp.GetRequiredService<RuntimeCallerIdentity>());
        services.AddHostedService(sp => new RuntimeCallerStartupCheck(
            sp.GetRequiredService<RuntimeCallerIdentity>(),
            sp.GetRequiredService<IOptions<RuntimeCallerOptions>>(),
            configuration,
            sp.GetRequiredService<ILogger<RuntimeCallerStartupCheck>>(),
            runsFlowScripts));

        return services;
    }
}
