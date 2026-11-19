using BBT.Workflow.Authorization.SubEndpoints;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration of the sub-endpoint gate (vnext#1265).</summary>
public static class SubEndpointGateServiceCollectionExtensions
{
    /// <summary>
    /// Binds <c>Workflow:SubEndpointGate</c> (validated at startup) and registers <see cref="ISubEndpointGate"/>.
    /// Only the host that serves the runtime-internal endpoints (Orchestration) needs it.
    /// </summary>
    public static IServiceCollection AddSubEndpointGate(this IServiceCollection services)
    {
        services.AddOptions<SubEndpointGateOptions>()
            .BindConfiguration(SubEndpointGateOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SubEndpointGateOptions>, SubEndpointGateOptionsValidator>());
        services.TryAddScoped<ISubEndpointGate, SubEndpointGate>();
        return services;
    }
}
