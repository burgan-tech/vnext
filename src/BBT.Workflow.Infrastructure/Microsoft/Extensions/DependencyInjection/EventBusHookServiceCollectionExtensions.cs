using BBT.Aether.Events;
using BBT.Aether.Uow;
using BBT.Workflow.Infrastructure.EventBus;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring event bus with hook support.
/// </summary>
public static class EventBusHookServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Aether event bus with hook execution support to the service collection.
    /// This method wraps the standard Aether event bus with a decorator that executes
    /// hooks before publishing events.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configure">An action to configure event bus options.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// This method internally calls <c>AddAetherEventBus</c> with the provided options,
    /// then decorates the registered <see cref="IDistributedEventBus"/> with
    /// <see cref="HookedDistributedEventBus"/> to enable hook execution.
    /// </para>
    /// <para>
    /// Hooks are discovered via <see cref="BBT.Workflow.Events.Hooks.EventHookAttribute"/>
    /// on event types and must be registered in the DI container to be executed.
    /// </para>
    /// <para>
    /// Example usage:
    /// <code>
    /// services.AddEventBusWithHooks(options =>
    /// {
    ///     options.DefaultSource = "urn:myapp";
    ///     options.PrefixEnvironmentToTopic = true;
    ///     options.PubSubName = "pubsub";
    /// });
    /// 
    /// // Register your hooks
    /// services.AddScoped&lt;IEventPublishHook, MyCustomHook&gt;();
    /// </code>
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is null.
    /// </exception>
    public static IServiceCollection AddEventBusWithHooks(
        this IServiceCollection services,
        Action<AetherEventBusOptions> configure)
    {
        if (services == null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        // First, add the standard Aether event bus
        services.AddAetherEventBus(configure);

        // Now decorate it with the hooked implementation
        // We need to replace the IDistributedEventBus registration with our decorator
        services.Decorate<IDistributedEventBus>((inner, serviceProvider) =>
        {
            var uowManager = serviceProvider.GetRequiredService<IUnitOfWorkManager>();
            var logger = serviceProvider.GetRequiredService<ILogger<HookedDistributedEventBus>>();
            return new HookedDistributedEventBus(inner, serviceProvider, uowManager, logger);
        });

        return services;
    }

    /// <summary>
    /// Decorates a service registration with a decorator implementation.
    /// </summary>
    /// <typeparam name="TService">The service type to decorate.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="decorator">Factory function to create the decorator.</param>
    /// <returns>The service collection for chaining.</returns>
    /// <remarks>
    /// This is a helper method to implement the decorator pattern in DI.
    /// It finds the existing service registration and wraps it with the decorator.
    /// </remarks>
    private static IServiceCollection Decorate<TService>(
        this IServiceCollection services,
        Func<TService, IServiceProvider, TService> decorator)
        where TService : class
    {
        // Find the existing service descriptor
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(TService));
        if (descriptor == null)
        {
            throw new InvalidOperationException(
                $"Service of type {typeof(TService).Name} is not registered. " +
                "Ensure AddAetherEventBus is called before decoration.");
        }

        // Remove the existing registration
        services.Remove(descriptor);

        // Create a new descriptor that wraps the original
        ServiceDescriptor decoratedDescriptor;

        if (descriptor.ImplementationInstance != null)
        {
            // Instance registration
            var instance = (TService)descriptor.ImplementationInstance;
            decoratedDescriptor = ServiceDescriptor.Describe(
                typeof(TService),
                sp => decorator(instance, sp),
                descriptor.Lifetime);
        }
        else if (descriptor.ImplementationFactory != null)
        {
            // Factory registration
            decoratedDescriptor = ServiceDescriptor.Describe(
                typeof(TService),
                sp =>
                {
                    var inner = (TService)descriptor.ImplementationFactory(sp);
                    return decorator(inner, sp);
                },
                descriptor.Lifetime);
        }
        else if (descriptor.ImplementationType != null)
        {
            // Type registration
            decoratedDescriptor = ServiceDescriptor.Describe(
                typeof(TService),
                sp =>
                {
                    var inner = (TService)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
                    return decorator(inner, sp);
                },
                descriptor.Lifetime);
        }
        else
        {
            throw new InvalidOperationException(
                $"Service descriptor for {typeof(TService).Name} has no implementation.");
        }

        services.Add(decoratedDescriptor);
        return services;
    }
}
