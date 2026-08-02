using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class ScriptContextRelatedTests
{
    private static readonly Guid InstanceId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ParentId = Guid.Parse("77777777-7777-7777-7777-777777777777");

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

    private static RelatedInstanceAccessor Accessor(Instance instance, IRelatedInstanceReader reader) =>
        new(instance, reader, Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(), NullLogger.Instance);

    private static IRelatedInstanceReader OkReader()
    {
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(new RelatedInstanceSnapshot
            {
                InstanceId = ParentId,
                Domain = "lending",
                Flow = "loan-application",
                Status = "A"
            }));
        return reader;
    }

    [Fact]
    public void Related_ShouldDefaultToTheNullAccessor()
    {
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>()).Build();

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
        context.Related.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task Related_ShouldExposeTheConfiguredAccessor()
    {
        var instance = ChildWithParent();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, OkReader()))
            .Build();

        context.Related.HasParent.ShouldBeTrue();
        var parent = await context.Related.ParentAsync(CancellationToken.None);
        parent!.InstanceId.ShouldBe(ParentId);
    }

    [Fact]
    public async Task CreateParallelBranch_ShouldShareTheAccessorMemo()
    {
        var instance = ChildWithParent();
        var reader = OkReader();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, reader))
            .Build();

        await context.Related.ParentAsync(CancellationToken.None);
        var branch = context.CreateParallelBranch();
        await branch.Related.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dispose_ShouldClearTheMemoAndResetToTheNullAccessor()
    {
        var instance = ChildWithParent();
        var reader = OkReader();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, reader))
            .Build();

        await context.Related.ParentAsync(CancellationToken.None);
        context.Dispose();

        // Related now throws once disposed (see Related_ShouldThrow_AfterDispose) rather than quietly
        // answering from the null accessor — a disposed context must not make a definite claim about
        // whether this instance has a parent.
        Should.Throw<ObjectDisposedException>(() => context.Related);
    }

    [Fact]
    public void CreateParallelBranch_ShouldGiveTheBranchItsOwnAccessor()
    {
        // The memo-sharing test passes even if CreateParallelBranch just assigned the coordinator's
        // accessor, because the memo is shared either way. This pins that ForBranch is actually called.
        var instance = ChildWithParent();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, OkReader()))
            .Build();

        var branch = context.CreateParallelBranch();

        branch.Related.ShouldNotBeSameAs(context.Related);
        branch.Related.ShouldBeOfType<RelatedInstanceAccessor>();
    }

    [Fact]
    public async Task Dispose_ShouldClearTheMemo_NotJustResetTheProperty()
    {
        // The existing dispose test only checks the property was reset; nothing proved ClearMemo ran.
        // Hold a direct reference to the accessor so it survives the property reset.
        var instance = ChildWithParent();
        var reader = OkReader();
        var accessor = Accessor(instance, reader);
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(accessor)
            .Build();

        await accessor.ParentAsync(CancellationToken.None);
        context.Dispose();
        await accessor.ParentAsync(CancellationToken.None);

        // Two reads: the memo was emptied by Dispose, so the second call hit the reader again.
        await reader.Received(2).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Related_ShouldThrow_AfterDispose()
    {
        var instance = ChildWithParent();
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(instance)
            .SetRelated(Accessor(instance, OkReader()))
            .Build();

        context.Dispose();

        Should.Throw<ObjectDisposedException>(() => context.Related);
    }

    [Fact]
    public void SetRelated_ShouldLeaveTheDefault_WhenGivenNull()
    {
        // Task 7's BuildRelatedAccessor returns null when no reader is registered and calls
        // SetRelated unconditionally, so this no-op is load-bearing.
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetInstance(ChildWithParent())
            .SetRelated(null)
            .Build();

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
    }
}
