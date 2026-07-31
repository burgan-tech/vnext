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

        context.Related.ShouldBeSameAs(NullRelatedInstanceAccessor.Instance);
    }
}
