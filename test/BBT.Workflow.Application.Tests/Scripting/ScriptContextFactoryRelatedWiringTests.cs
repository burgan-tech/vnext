using System;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Regression coverage for a bug where <see cref="ScriptContextFactory"/> never forwarded the
/// related-instance dependencies (<see cref="IRelatedInstanceReader"/>,
/// <see cref="IInstanceCorrelationRepository"/>, <see cref="RelatedAccessOptions"/>) to
/// <see cref="ScriptContextBuilder"/> — every <c>NewBuilder(...)</c> call silently produced a
/// <see cref="NullRelatedInstanceAccessor"/>, in every host, regardless of what was registered in DI.
/// </summary>
public sealed class ScriptContextFactoryRelatedWiringTests
{
    private static readonly Guid InstanceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ParentId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    private static Instance ChildWithParent()
    {
        var instance = Instance.Create(InstanceId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = "lending",
            [DomainConsts.MetaDataKeys.Flow] = "loan-application"
        });
        return instance;
    }

    /// <summary>
    /// Directly constructs <see cref="ScriptContextFactory"/> (no DI container) supplying every
    /// optional dependency, and asserts the resulting <see cref="ScriptContext.Related"/> is a real
    /// <see cref="RelatedInstanceAccessor"/>. This pins down <c>NewBuilder</c>'s forwarding behavior in
    /// isolation from DI wiring/lifetime concerns.
    /// </summary>
    [Fact]
    public async Task NewBuilder_ShouldForwardEveryOptionalDependency_ToTheBuilder()
    {
        var factory = new ScriptContextFactory(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance,
            rawBodyProvider: null,
            relatedInstanceReader: Substitute.For<IRelatedInstanceReader>(),
            correlationRepository: Substitute.For<IInstanceCorrelationRepository>(),
            relatedAccessOptions: Microsoft.Extensions.Options.Options.Create(new RelatedAccessOptions()));

        var context = await factory
            .NewBuilder(Substitute.For<IInstanceRepository>())
            .WithInstance(ChildWithParent())
            .BuildAsync();

        context.Related.ShouldBeOfType<RelatedInstanceAccessor>();
        context.Related.HasParent.ShouldBeTrue();
    }

    /// <summary>
    /// End-to-end: resolves the real <see cref="IScriptContextFactory"/> from a DI container wired the
    /// way the composition root wires it — <c>AddTaskHandlers()</c> (which registers
    /// <see cref="ScriptContextFactory"/> and binds <see cref="RelatedAccessOptions"/>) plus the
    /// gateway services the related-instance feature depends on. Before the fix, the factory was a
    /// singleton with a constructor that dropped these dependencies on the floor, so this would have
    /// produced a <see cref="NullRelatedInstanceAccessor"/> no matter what was registered.
    /// </summary>
    [Fact]
    public async Task RealFactoryResolvedFromContainer_ShouldProduceARealRelatedAccessor()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddSingleton(_ => Substitute.For<IComponentCacheStore>());
        services.AddScoped(_ => Substitute.For<IRelatedInstanceReader>());
        services.AddScoped(_ => Substitute.For<IInstanceCorrelationRepository>());
        services.AddTaskHandlers();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IScriptContextFactory>();

        var context = await factory
            .NewBuilder(Substitute.For<IInstanceRepository>())
            .WithInstance(ChildWithParent())
            .BuildAsync();

        context.Related.ShouldBeOfType<RelatedInstanceAccessor>();
        context.Related.HasParent.ShouldBeTrue();
    }
}
