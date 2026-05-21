using BBT.Workflow.Infrastructure.Dapr;
using BBT.Workflow.Infrastructure.Dapr.Notification;
using BBT.Workflow.Tasks.Notification;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering multi-channel Dapr notification services.
/// </summary>
public static class DaprNotificationServiceCollectionExtensions
{
    /// <summary>
    /// Adds Dapr notification services: channel resolver and configuration options.
    /// </summary>
    public static IServiceCollection AddDaprNotification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DaprNotificationOptions>(
            configuration.GetSection(DaprNotificationOptions.SectionName));

        services.AddNotificationChannelResolver();

        return services;
    }

    /// <summary>
    /// Adds Dapr notification services with custom options configuration.
    /// </summary>
    public static IServiceCollection AddDaprNotification(
        this IServiceCollection services,
        Action<DaprNotificationOptions> configureOptions)
    {
        services.Configure(configureOptions);
        services.AddNotificationChannelResolver();

        return services;
    }

    /// <summary>
    /// Registers the <see cref="INotificationChannelResolver"/> singleton.
    /// Called from both <see cref="AddDaprNotification(IServiceCollection, IConfiguration)"/>
    /// and <c>AddInfrastructureRuntimeServices</c> to ensure the resolver is available
    /// in all host configurations (Orchestration, Execution, Workers).
    /// </summary>
    public static IServiceCollection AddNotificationChannelResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<INotificationChannelResolver, NotificationChannelResolver>();
        return services;
    }
}
