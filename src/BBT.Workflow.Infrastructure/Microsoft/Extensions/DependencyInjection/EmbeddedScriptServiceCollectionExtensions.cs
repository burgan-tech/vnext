using BBT.Workflow.Infrastructure.Scripting;
using BBT.Workflow.Scripting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for registering embedded script services in an <see cref="IServiceCollection"/>.
/// </summary>
public static class EmbeddedScriptServiceCollectionExtensions
{
    /// <summary>
    /// Adds embedded script provider services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// This method registers the following services:
    /// <list type="bullet">
    /// <item><description><see cref="IEmbeddedScriptProvider"/> as singleton</description></item>
    /// <item><description><see cref="INotificationScriptProvider"/> as singleton</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Before calling this method, configure <see cref="EmbeddedScriptOptions"/> to map script keys
    /// to their embedded resource names and source assemblies:
    /// <code>
    /// services.ConfigureEmbeddedScripts(options =>
    /// {
    ///     options.Add("notification.default", 
    ///         "BBT.Workflow.Tasks.Scripting.NotificationMapping.csx",
    ///         typeof(SomeTypeInTargetAssembly).Assembly);
    /// });
    /// </code>
    /// </para>
    /// </remarks>
    public static IServiceCollection AddEmbeddedScriptServices(this IServiceCollection services)
    {
        // Scripts are loaded lazily and cached in memory
        services.AddSingleton<IEmbeddedScriptProvider, EmbeddedScriptProvider>();

        return services;
    }

    /// <summary>
    /// Configures embedded script options with script key to resource and assembly mappings.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configure">A delegate to configure <see cref="EmbeddedScriptOptions"/>.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    /// <example>
    /// <code>
    /// services.ConfigureEmbeddedScripts(options =>
    /// {
    ///     options.Add("notification.default", 
    ///         "BBT.Workflow.Tasks.Scripting.NotificationMapping.csx",
    ///         typeof(EmbeddedScriptEntry).Assembly);
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection ConfigureEmbeddedScripts(
        this IServiceCollection services,
        Action<EmbeddedScriptOptions> configure)
    {
        services.Configure(configure);
        return services;
    }
}

