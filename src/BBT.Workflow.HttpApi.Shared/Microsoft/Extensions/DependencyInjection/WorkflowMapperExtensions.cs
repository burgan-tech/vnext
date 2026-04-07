namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Mapper module: Aether Mapperly mapper registration with assembly scanning.
/// </summary>
public static class WorkflowMapperExtensions
{
    /// <summary>
    /// Registers Aether Mapperly mapper scanning assemblies from HttpApi.Shared, Domain, and Application modules.
    /// </summary>
    public static IServiceCollection AddWorkflowMapper(this IServiceCollection services)
    {
        services.AddAetherMapperlyMapper(
        [
            typeof(WorkflowAspNetCoreExtensions), // HttpApi.Shared
            typeof(WorkflowDomainModuleServiceCollectionExtensions), // Domain
            typeof(WorkflowApplicationModuleServiceCollectionExtensions) // Application
        ]);
        return services;
    }
}
