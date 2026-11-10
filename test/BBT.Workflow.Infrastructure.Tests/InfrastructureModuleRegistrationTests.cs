using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.DistributedCache;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Files;
using BBT.Workflow.Schemas;
using BBT.Workflow.Scripting.Functions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.DependencyInjection;

/// <summary>
/// Pins the split between <c>AddInfrastructureModule</c> (callable by minimal hosts such as
/// DbMigrator, which register no distributed cache) and <c>AddInfrastructureRuntimeServices</c>
/// (runtime hosts only). Registering a cache-dependent service in the core module made DbMigrator
/// fail at <c>ValidateOnBuild</c> with "Unable to resolve service for type
/// 'BBT.Aether.DistributedCache.IDistributedCacheService'".
/// </summary>
public sealed class InfrastructureModuleRegistrationTests
{
    [Fact]
    public void Core_module_registers_nothing_that_needs_the_distributed_cache()
    {
        var services = BuildCoreModule();

        var offenders = services
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null)
            .Distinct()
            .Where(RequiresDistributedCache!)
            .Select(type => type!.FullName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Services needing IDistributedCacheService belong in AddInfrastructureRuntimeServices; "
            + "minimal hosts (DbMigrator) call AddInfrastructureModule without a distributed cache.");
    }

    [Fact]
    public void Attribute_index_catalog_is_a_runtime_only_service()
    {
        var core = BuildCoreModule();
        core.Any(descriptor => descriptor.ServiceType == typeof(IAttributeIndexCatalog))
            .ShouldBeFalse();

        var runtime = BuildCoreModule();
        runtime.AddInfrastructureRuntimeServices();

        runtime.Single(descriptor => descriptor.ServiceType == typeof(IAttributeIndexCatalog))
            .ImplementationType.ShouldBe(typeof(PostgresAttributeIndexService));
    }

    /// <summary>
    /// Services provided only by <c>AddApplicationModule</c>. DbMigrator registers the Domain and
    /// Infrastructure modules without it, so nothing in the core Infrastructure module may take one
    /// as a required constructor dependency — <c>ScriptFileWriter</c> once did and broke DbMigrator.
    /// </summary>
    private static readonly Type[] ApplicationOnlyServices =
    [
        typeof(IComponentCacheStore),
        typeof(IFileOffloadService),
    ];

    [Fact]
    public void Core_module_registers_nothing_that_needs_an_application_only_service()
    {
        var services = BuildCoreModule();
        services.AddDomainModule();

        // Guard: the list must stay "application only". If the core modules start registering one of
        // these, the test below would silently stop protecting anything.
        services.Select(descriptor => descriptor.ServiceType)
            .Intersect(ApplicationOnlyServices)
            .ShouldBeEmpty();

        var offenders = services
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null)
            .Distinct()
            .Where(RequiresApplicationOnlyService!)
            .Select(type => type!.FullName)
            .ToArray();

        offenders.ShouldBeEmpty(
            "Services needing an Application-module service belong in AddInfrastructureRuntimeServices; "
            + "DbMigrator calls AddInfrastructureModule without AddApplicationModule.");
    }

    [Fact]
    public void Script_file_writer_is_a_runtime_only_service()
    {
        var core = BuildCoreModule();
        core.Any(descriptor => descriptor.ServiceType == typeof(IScriptFileWriter))
            .ShouldBeFalse();

        var runtime = BuildCoreModule();
        runtime.AddInfrastructureRuntimeServices();

        runtime.Single(descriptor => descriptor.ServiceType == typeof(IScriptFileWriter))
            .ImplementationType.ShouldBe(typeof(ScriptFileWriter));
    }

    private static bool RequiresApplicationOnlyService(Type? type)
        => type is { IsAbstract: false }
           && type.GetConstructors()
               .Any(constructor => constructor.GetParameters()
                   .Any(parameter => !parameter.IsOptional
                                     && ApplicationOnlyServices.Contains(parameter.ParameterType)));

    private static ServiceCollection BuildCoreModule()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] =
                    "Host=localhost;Database=workflow;Username=postgres;Password=postgres"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructureModule(configuration);
        return services;
    }

    private static bool RequiresDistributedCache(Type? type)
        => type is { IsAbstract: false }
           && type.GetConstructors()
               .Any(constructor => constructor.GetParameters()
                   .Any(parameter => parameter.ParameterType == typeof(IDistributedCacheService)));
}
