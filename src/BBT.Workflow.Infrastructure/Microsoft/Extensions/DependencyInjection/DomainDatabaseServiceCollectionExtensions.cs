using BBT.Aether.BackgroundJob;
using BBT.Aether.BackgroundJob.Processing;
using BBT.Aether.Events;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wiring for multi-domain hosting with a dedicated database per co-hosted domain
/// (<c>ConnectionStrings:Domains:&lt;domain&gt;</c>). Without that section every call here is a no-op in
/// behaviour: the default connection string is used exactly as before.
/// </summary>
public static class DomainDatabaseServiceCollectionExtensions
{
    /// <summary>Configuration section holding the dedicated databases, one key per co-hosted domain.</summary>
    public const string DomainsSection = "ConnectionStrings:Domains";

    /// <summary>
    /// Binds <see cref="DomainDatabaseOptions"/> from <c>ConnectionStrings:Default</c> and
    /// <c>ConnectionStrings:Domains:*</c>.
    /// </summary>
    public static IServiceCollection AddDomainDatabases(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DomainDatabaseOptions>(options =>
        {
            options.DefaultConnectionString = configuration.GetConnectionString("Default");
            foreach (var entry in configuration.GetSection(DomainsSection).GetChildren())
            {
                if (!string.IsNullOrWhiteSpace(entry.Value))
                {
                    options.Domains[entry.Key] = entry.Value;
                }
            }
        });

        return services;
    }

    /// <summary>
    /// Routes <typeparamref name="TDbContext"/> to the current domain's database. Call after
    /// <c>AddAetherNpgsql&lt;TDbContext&gt;</c>: it decorates the unit-of-work configurator that call
    /// registered, and adds an options step for the directly resolved context (migrations,
    /// <c>EnsureCreated</c>), which runs after Aether's and only replaces the connection string.
    /// </summary>
    public static IServiceCollection RouteToDomainDatabase<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IAetherDbContextConfigurator<TDbContext>))
                         ?? throw new InvalidOperationException(
                             $"RouteToDomainDatabase<{typeof(TDbContext).Name}> must be called after AddAetherNpgsql<{typeof(TDbContext).Name}>.");

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IAetherDbContextConfigurator<TDbContext>),
            sp => new DomainAwareDbContextConfigurator<TDbContext>(
                (IAetherDbContextConfigurator<TDbContext>)CreateInner(descriptor, sp),
                sp.GetRequiredService<IDomainDatabaseResolver>()),
            descriptor.Lifetime));

        services.ConfigureDbContext<TDbContext>((sp, builder) =>
        {
            var resolver = sp.GetRequiredService<IDomainDatabaseResolver>();
            if (resolver.HasDedicatedDatabase() && resolver.ConnectionStringFor() is { } connectionString)
            {
                builder.UseNpgsql(connectionString);
            }
        });

        return services;
    }

    /// <summary>
    /// Processes every database's outbox: decorates the <see cref="IOutboxProcessor"/> registered by
    /// <c>AddAetherOutbox</c> (call after it). One database → plain pass-through.
    /// </summary>
    public static IServiceCollection FanOutOutboxPerDomainDatabase(this IServiceCollection services) =>
        services.Decorate<IOutboxProcessor>((inner, sp) =>
            new DomainFanOutOutboxProcessor(inner, sp.GetRequiredService<IDomainDatabaseResolver>()));

    /// <summary>
    /// Arms every database's background jobs and claims each Dapr job in its domain's database (the domain
    /// travels in the job name, <c>vnext.job.v2</c>). Call after
    /// <c>AddAetherBackgroundJob</c> / <c>AddDaprJobScheduler</c>. One database → unchanged behaviour.
    /// </summary>
    public static IServiceCollection FanOutBackgroundJobsPerDomainDatabase(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<BackgroundJobArmingProcessor, DomainFanOutBackgroundJobArmingProcessor>());
        if (services.Any(d => d.ServiceType == typeof(IJobExecutionBridge)))
        {
            services.Decorate<IJobExecutionBridge>((inner, _) => new DomainScopedJobExecutionBridge(inner));
        }

        return services;
    }

    private static IServiceCollection Decorate<TService>(
        this IServiceCollection services,
        Func<TService, IServiceProvider, TService> decorate)
        where TService : class
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(TService))
                         ?? throw new InvalidOperationException($"No {typeof(TService).Name} is registered to decorate.");
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(TService),
            sp => decorate((TService)CreateInner(descriptor, sp), sp),
            descriptor.Lifetime));
        return services;
    }

    private static object CreateInner(ServiceDescriptor descriptor, IServiceProvider sp) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(sp)
        ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
}
