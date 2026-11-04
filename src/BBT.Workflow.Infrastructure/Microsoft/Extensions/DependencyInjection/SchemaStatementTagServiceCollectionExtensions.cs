using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Workflow.Data;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Wiring for <see cref="SchemaStatementTagDbContextConfigurator{TDbContext}"/>.
/// </summary>
public static class SchemaStatementTagServiceCollectionExtensions
{
    /// <summary>
    /// Makes the command spans of <typeparamref name="TDbContext"/>'s unit-of-work contexts name the real
    /// schema instead of Aether's placeholder. Call after <c>AddAetherNpgsql&lt;TDbContext&gt;</c> (and
    /// after <c>RouteToDomainDatabase&lt;TDbContext&gt;</c> when both are used): it decorates the
    /// unit-of-work configurator registered last. Changes only the span tag.
    /// </summary>
    public static IServiceCollection TagStatementsWithSchema<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IAetherDbContextConfigurator<TDbContext>))
                         ?? throw new InvalidOperationException(
                             $"TagStatementsWithSchema<{typeof(TDbContext).Name}> must be called after AddAetherNpgsql<{typeof(TDbContext).Name}>.");

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(IAetherDbContextConfigurator<TDbContext>),
            sp => new SchemaStatementTagDbContextConfigurator<TDbContext>(
                (IAetherDbContextConfigurator<TDbContext>)(descriptor.ImplementationInstance
                    ?? descriptor.ImplementationFactory?.Invoke(sp)
                    ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!))),
            descriptor.Lifetime));

        return services;
    }
}
