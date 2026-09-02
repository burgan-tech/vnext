using BBT.Aether.Events;
using BBT.Aether.Tracing;
using BBT.Workflow.Infrastructure.EventBus;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Extension methods for configuring the event bus with trace-stamping support.
/// </summary>
public static class EventBusHookServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Aether event bus to the service collection, decorated with
    /// <see cref="TraceStampingDistributedEventBus"/> so every published event carries W3C trace
    /// context, the originating request id, and trace-lane anchors.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <param name="configure">An action to configure event bus options.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
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

        // Now decorate it with the trace-stamping implementation
        // We need to replace the IDistributedEventBus registration with our decorator
        services.Decorate<IDistributedEventBus>((inner, serviceProvider) =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<TraceStampingDistributedEventBus>>();
            // The old HookedDistributedEventBus construction never passed this optional provider, so
            // RequestId stamping was a permanent no-op in production. Resolving it here deliberately
            // activates it — an additive CloudEvent extension field; consumers that don't read
            // requestId are unaffected, and the Inbox side (EventTraceScope) already expects it.
            var correlationIdProvider = serviceProvider.GetService<ICorrelationIdProvider>();
            return new TraceStampingDistributedEventBus(inner, logger, correlationIdProvider);
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
