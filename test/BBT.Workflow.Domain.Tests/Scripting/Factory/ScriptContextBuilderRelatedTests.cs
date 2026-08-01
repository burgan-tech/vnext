using System;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Workflow;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting.Factory;

/// <summary>
/// Task 7: verifies <see cref="ScriptContextBuilder"/> constructs a real
/// <see cref="RelatedInstanceAccessor"/> when the optional related-instance dependencies are supplied,
/// and falls back to <see cref="NullRelatedInstanceAccessor"/> (via <c>ScriptContext.Builder</c>'s
/// default) when either dependency is missing — the path every existing consumer takes until Task 11
/// registers <see cref="IRelatedInstanceReader"/> in DI.
/// </summary>
public class ScriptContextBuilderRelatedTests
{
    private static readonly Guid InstanceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

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

    private static ScriptContextBuilder CreateBuilder(
        IRelatedInstanceReader? reader = null,
        IInstanceCorrelationRepository? correlationRepository = null) =>
        new(
            Substitute.For<IComponentCacheStore>(),
            Substitute.For<IInstanceRepository>(),
            NullLogger<ScriptContext>.Instance,
            relatedInstanceReader: reader,
            correlationRepository: correlationRepository,
            relatedAccessOptions: Options.Create(new RelatedAccessOptions()));

    [Fact]
    public async Task BuildAsync_ShouldProduceARealAccessor_WhenBothOptionalDependenciesAreSupplied()
    {
        var instance = ChildWithParent();
        var builder = CreateBuilder(
            Substitute.For<IRelatedInstanceReader>(),
            Substitute.For<IInstanceCorrelationRepository>());

        var context = await builder.WithInstance(instance).BuildAsync();

        context.Related.ShouldBeOfType<RelatedInstanceAccessor>();
        context.Related.HasParent.ShouldBeTrue();
    }

    [Fact]
    public async Task BuildAsync_ShouldFallBackToTheNullAccessor_WhenTheReaderIsMissing()
    {
        var instance = ChildWithParent();
        var builder = CreateBuilder(
            reader: null,
            correlationRepository: Substitute.For<IInstanceCorrelationRepository>());

        var context = await builder.WithInstance(instance).BuildAsync();

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
    }

    [Fact]
    public async Task BuildAsync_ShouldFallBackToTheNullAccessor_WhenTheCorrelationRepositoryIsMissing()
    {
        var instance = ChildWithParent();
        var builder = CreateBuilder(
            reader: Substitute.For<IRelatedInstanceReader>(),
            correlationRepository: null);

        var context = await builder.WithInstance(instance).BuildAsync();

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
    }
}
