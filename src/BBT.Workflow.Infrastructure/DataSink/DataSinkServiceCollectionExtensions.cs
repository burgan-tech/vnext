using BBT.Workflow.DataSink;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Infrastructure.DataSink;

/// <summary>
/// Extension methods for setting up DataSink services in an <see cref="IServiceCollection" />.
/// </summary>
public static class DataSinkServiceCollectionExtensions
{
    /// <summary>
    /// Adds DataSink services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddDataSinkServices(this IServiceCollection services)
    {
        // Register core DataSink services
        services.AddSingleton<IDataSinkRegistry, DataSinkRegistry>();
        services.AddScoped<IDataSinkManager, DataSinkManager>();

        return services;
    }

    /// <summary>
    /// Registers all DataSink implementations with the registry
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection RegisterDataSinks(this IServiceCollection services)
    {
        services.AddHostedService<DataSinkRegistrationHostedService>();
        return services;
    }
}
