using BBT.Workflow.ClickHouse;
using BBT.Workflow.DataSink;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    /// Adds ClickHouse DataSink services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection" /> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection" /> so that additional calls can be chained.</returns>
    public static IServiceCollection AddClickHouseDataSinks(this IServiceCollection services)
    {
        // Configure ClickHouse options
        services.Configure<ClickHouseConfiguration>(services.GetConfiguration().GetSection("ClickHouse"));

        // Register HttpClient for ClickHouse
        services.AddHttpClient<ClickHouseInstanceDataSink>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var clickHouseConfig = configuration.GetSection("ClickHouse").Get<ClickHouseConfiguration>();
            
            if (clickHouseConfig?.Enabled == true && !string.IsNullOrEmpty(clickHouseConfig.ConnectionString))
            {
                var baseUrl = ExtractBaseUrlFromConnectionString(clickHouseConfig.ConnectionString);
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);
                }
            }
        });

        services.AddHttpClient<ClickHouseInstanceTransitionDataSink>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var clickHouseConfig = configuration.GetSection("ClickHouse").Get<ClickHouseConfiguration>();
            
            if (clickHouseConfig?.Enabled == true && !string.IsNullOrEmpty(clickHouseConfig.ConnectionString))
            {
                var baseUrl = ExtractBaseUrlFromConnectionString(clickHouseConfig.ConnectionString);
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);
                }
            }
        });

        services.AddHttpClient<ClickHouseInstanceTaskDataSink>((serviceProvider, client) =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var clickHouseConfig = configuration.GetSection("ClickHouse").Get<ClickHouseConfiguration>();
            
            if (clickHouseConfig?.Enabled == true && !string.IsNullOrEmpty(clickHouseConfig.ConnectionString))
            {
                var baseUrl = ExtractBaseUrlFromConnectionString(clickHouseConfig.ConnectionString);
                if (!string.IsNullOrEmpty(baseUrl))
                {
                    client.BaseAddress = new Uri(baseUrl);
                    client.Timeout = TimeSpan.FromSeconds(30);
                }
            }
        });

        // Register ClickHouse DataSink implementations
        services.AddSingleton<IDataSink<Instance>, ClickHouseInstanceDataSink>();
        services.AddSingleton<IDataSink<InstanceTransition>, ClickHouseInstanceTransitionDataSink>();
        services.AddSingleton<IDataSink<InstanceTask>, ClickHouseInstanceTaskDataSink>();

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

    /// <summary>
    /// Extracts base URL from ClickHouse connection string
    /// </summary>
    /// <param name="connectionString">ClickHouse connection string</param>
    /// <returns>Base URL for ClickHouse HTTP interface</returns>
    private static string? ExtractBaseUrlFromConnectionString(string connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        // Parse connection string format: Host=localhost;Port=8123;Database=workflow_analytics;Username=default;Password=;
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        string? host = null;
        string? port = null;

        foreach (var part in parts)
        {
            var keyValue = part.Split('=', 2);
            if (keyValue.Length == 2)
            {
                var key = keyValue[0].Trim();
                var value = keyValue[1].Trim();

                switch (key.ToLowerInvariant())
                {
                    case "host":
                        host = value;
                        break;
                    case "port":
                        port = value;
                        break;
                    default:
                    // Unwanted parts
                        break;
                }
            }
        }

        if (!string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(port))
        {
            return $"http://{host}:{port}";
        }

        return null;
    }

    /// <summary>
    /// Gets configuration from service provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <returns>Configuration instance</returns>
    private static IConfiguration GetConfiguration(this IServiceCollection services)
    {
        var serviceProvider = services.BuildServiceProvider();
        return serviceProvider.GetRequiredService<IConfiguration>();
    }
}
