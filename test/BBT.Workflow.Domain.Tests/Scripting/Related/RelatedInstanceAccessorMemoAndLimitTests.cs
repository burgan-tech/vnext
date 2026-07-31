using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class RelatedInstanceAccessorMemoAndLimitTests
{
    private static readonly Guid InstanceId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ParentId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static Instance ChildWithParent()
    {
        var instance = Instance.Create(InstanceId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = "lending",
            [DomainConsts.MetaDataKeys.Flow] = "loan-application",
            [DomainConsts.MetaDataKeys.Version] = "2.1.0"
        });
        return instance;
    }

    private static IRelatedInstanceReader ReaderReturning(Guid id)
    {
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(new RelatedInstanceSnapshot
            {
                InstanceId = id,
                Domain = "lending",
                Flow = "loan-application",
                Status = "A"
            }));
        return reader;
    }

    [Fact]
    public async Task ParentAsync_ShouldReadOnceAndServeTheMemoAfterwards()
    {
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        var first = await accessor.ParentAsync(CancellationToken.None);
        var second = await accessor.ParentAsync(CancellationToken.None);

        first.ShouldBeSameAs(second);
        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resolution_ShouldThrow_WhenCapIsExceeded()
    {
        var instance = Instance.Create(InstanceId, "loan-application", "2.1.0");

        var correlations = Enumerable.Range(0, 3)
            .Select(index => InstanceCorrelation.Create(
                Guid.NewGuid(), InstanceId, "awaiting-sub",
                Guid.Parse($"dddddddd-0000-0000-0000-00000000000{index}"),
                "P", "compliance", $"flow-{index}", "1.0.0"))
            .ToList();

        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(correlations);

        var reader = Substitute.For<IRelatedInstanceReader>();
        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository,
            new RelatedAccessOptions { MaxResolutionsPerContext = 2 },
            NullLogger.Instance);

        var exception = await Should.ThrowAsync<RelatedInstanceAccessException>(
            () => accessor.SubsAsync(null, CancellationToken.None));

        exception.Message.ShouldContain("limit of 2");
        await reader.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default);
    }

    [Fact]
    public async Task ForBranch_ShouldShareTheMemoWithTheOriginal()
    {
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.ParentAsync(CancellationToken.None);
        var branch = accessor.ForBranch(ChildWithParent());
        await branch.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearMemo_ShouldForceTheNextReadToHitTheReader()
    {
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.ParentAsync(CancellationToken.None);
        accessor.ClearMemo();
        await accessor.ParentAsync(CancellationToken.None);

        await reader.Received(2).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForBranch_ShouldShareTheMemoBidirectionally()
    {
        // The existing ForBranch test resolves on the coordinator first, so a ForBranch that COPIED
        // the dictionary would also pass. Resolving on the branch first and then on the coordinator
        // only passes if the memo is genuinely shared.
        var reader = ReaderReturning(ParentId);
        var accessor = new RelatedInstanceAccessor(
            ChildWithParent(), reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

        var branch = accessor.ForBranch(ChildWithParent());
        await branch.ParentAsync(CancellationToken.None);
        await accessor.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearMemo_ShouldAlsoResetTheCorrelationCache()
    {
        // The existing ClearMemo test only calls ParentAsync, which never touches the correlation
        // cache — so the `_correlationCache.Items = null` half was untested.
        var instanceId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var instance = Instance.Create(instanceId, "loan-application", "2.1.0");

        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns([
                InstanceCorrelation.Create(
                    Guid.NewGuid(), instanceId, "awaiting-sub", Guid.NewGuid(),
                    "S", "compliance", "kyc-flow", "1.0.0")
            ]);

        var accessor = new RelatedInstanceAccessor(
            instance, Substitute.For<IRelatedInstanceReader>(), correlationRepository,
            new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.SubKeysAsync(CancellationToken.None);
        accessor.ClearMemo();
        await accessor.SubKeysAsync(CancellationToken.None);

        await correlationRepository.Received(2).GetByParentAsync(instanceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ForBranch_ShouldShareTheCorrelationCache_ForTheSameInstance()
    {
        var instanceId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var instance = Instance.Create(instanceId, "loan-application", "2.1.0");

        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns([
                InstanceCorrelation.Create(
                    Guid.NewGuid(), instanceId, "awaiting-sub", Guid.NewGuid(),
                    "S", "compliance", "kyc-flow", "1.0.0")
            ]);

        var accessor = new RelatedInstanceAccessor(
            instance, Substitute.For<IRelatedInstanceReader>(), correlationRepository,
            new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.SubKeysAsync(CancellationToken.None);
        var branch = accessor.ForBranch(Instance.Create(instanceId, "loan-application", "2.1.0"));
        await branch.SubKeysAsync(CancellationToken.None);

        // One load across coordinator and branch — the branch must not re-query the same parent.
        await correlationRepository.Received(1).GetByParentAsync(instanceId, Arg.Any<CancellationToken>());
    }
}
