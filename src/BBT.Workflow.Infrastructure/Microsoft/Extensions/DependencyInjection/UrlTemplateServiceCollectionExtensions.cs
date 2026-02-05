using BBT.Workflow.Definitions;
using BBT.Workflow.Infrastructure.Definitions;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring URL template services in an <see cref="IServiceCollection" />.
/// </summary>
public static class UrlTemplateServiceCollectionExtensions
{
    /// <summary>
    /// Adds URL template services for generating client-facing HATEOAS URLs.
    /// Configures UrlTemplateOptions from appsettings and registers IUrlTemplateBuilder.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <param name="configuration">The configuration instance to bind UrlTemplateOptions from.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    /// <remarks>
    /// URL templates are used by controllers to generate HATEOAS links in API responses.
    /// Internal service-to-service URLs use static InstanceUrlTemplates and are not affected by this configuration.
    /// </remarks>
    public static IServiceCollection AddUrlTemplateServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure URL templates for client-facing HATEOAS responses
        services.Configure<UrlTemplateOptions>(configuration.GetSection(UrlTemplateOptions.SectionName));
        
        // Register URL template builder as singleton (stateless service)
        services.AddSingleton<IUrlTemplateBuilder, UrlTemplateBuilder>();
        
        return services;
    }
}
