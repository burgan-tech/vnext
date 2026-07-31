using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class RelatedInstanceAccessorSubTests
{
    private static readonly Guid ParentId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static InstanceCorrelation Correlation(
        Guid subInstanceId,
        string subFlowName,
        DateTime createdAt,
        string subFlowType = "S",
        string subFlowDomain = "compliance",
        bool completed = false,
        SubItemTerminalOutcome outcome = SubItemTerminalOutcome.Completed)
    {
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            ParentId,
            "awaiting-sub",
            subInstanceId,
            subFlowType,
            subFlowDomain,
            subFlowName,
            "1.0.0");
        ForceCreatedAt(correlation, createdAt);
        if (completed)
            correlation.ApplyTerminalOutcome(outcome, createdAt.AddMinutes(1));
        return correlation;
    }

    private static void ForceCreatedAt(InstanceCorrelation correlation, DateTime createdAt) =>
        typeof(InstanceCorrelation)
            .GetProperty(nameof(InstanceCorrelation.CreatedAt))!
            .SetValue(correlation, createdAt);

    private static RelatedInstanceSnapshot Snapshot(Guid id, string flow, string status = "C", bool isCompleted = true) => new()
    {
        InstanceId = id,
        Domain = "compliance",
        Flow = flow,
        FlowVersion = "1.0.0",
        Status = status,
        CurrentState = "done",
        IsCompleted = isCompleted
    };

    private static (RelatedInstanceAccessor Accessor, IRelatedInstanceReader Reader) CreateAccessor(
        params InstanceCorrelation[] correlations)
    {
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0", "customer-42");

        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns(correlations.ToList());

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var refs = call.Arg<IReadOnlyList<RelatedInstanceRef>>();
                IReadOnlyList<RelatedInstanceSnapshot> snapshots =
                    refs.Select(r => Snapshot(r.InstanceId, r.Flow)).ToList();
                return Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots));
            });

        var accessor = new RelatedInstanceAccessor(
            instance,
            reader,
            correlationRepository,
            new RelatedAccessOptions(),
            NullLogger.Instance);

        return (accessor, reader);
    }

    [Fact]
    public async Task SubAsync_ShouldReturnNull_WhenNoCorrelationMatchesTheKey()
    {
        var (accessor, _) = CreateAccessor(
            Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1)));

        var result = await accessor.SubAsync("doc-upload", CancellationToken.None);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task SubAsync_ShouldPickTheNewestCorrelationForTheKey()
    {
        var older = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var newer = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var (accessor, _) = CreateAccessor(
            Correlation(older, "doc-upload", new DateTime(2026, 1, 1)),
            Correlation(newer, "doc-upload", new DateTime(2026, 3, 1)));

        var result = await accessor.SubAsync("doc-upload", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.InstanceId.ShouldBe(newer);
    }

    [Fact]
    public async Task SubAsync_ShouldFindCompletedCorrelations()
    {
        var subId = Guid.NewGuid();
        var (accessor, _) = CreateAccessor(
            Correlation(subId, "kyc-flow", new DateTime(2026, 1, 1), completed: true));

        var result = await accessor.SubAsync("kyc-flow", CancellationToken.None);

        result.ShouldNotBeNull();
        result!.CorrelationCompleted.ShouldBe(true);
        result.TerminalOutcome.ShouldBe("Completed");
    }

    [Fact]
    public async Task SubAsync_ShouldKeepInstanceStatusAndCorrelationStateIndependent()
    {
        // Subflow completion window: the sub instance is Completed while the correlation is still open.
        var subId = Guid.NewGuid();
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0");
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([Correlation(subId, "kyc-flow", new DateTime(2026, 1, 1), completed: false)]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(
                (IReadOnlyList<RelatedInstanceSnapshot>)[Snapshot(subId, "kyc-flow", "C", isCompleted: true)])));

        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        var result = await accessor.SubAsync("kyc-flow", CancellationToken.None);

        result!.IsCompleted.ShouldBeTrue();          // instance reached C
        result.CorrelationCompleted.ShouldBe(false); // relationship still open
        result.TerminalOutcome.ShouldBeNull();
    }

    [Fact]
    public async Task SubAsync_ShouldExposeSubFlowTypeCode()
    {
        var subId = Guid.NewGuid();
        var (accessor, _) = CreateAccessor(
            Correlation(subId, "notify-flow", new DateTime(2026, 1, 1), subFlowType: "P"));

        var result = await accessor.SubAsync("notify-flow", CancellationToken.None);

        result!.SubFlowType.ShouldBe("P");
    }

    [Fact]
    public async Task SubsAsync_ShouldReturnEveryCorrelationOrderedByCreatedAt()
    {
        var first = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
        var second = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var (accessor, _) = CreateAccessor(
            Correlation(second, "doc-upload", new DateTime(2026, 3, 1)),
            Correlation(first, "kyc-flow", new DateTime(2026, 1, 1)));

        var result = await accessor.SubsAsync(null, CancellationToken.None);

        result.Select(r => r.InstanceId).ShouldBe([first, second]);
    }

    [Fact]
    public async Task SubsAsync_ShouldFilterByKey()
    {
        var (accessor, _) = CreateAccessor(
            Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 2, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 3, 1)));

        var result = await accessor.SubsAsync("doc-upload", CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldAllBe(r => r.Flow == "doc-upload");
    }

    [Fact]
    public async Task SubsAsync_ShouldBatchReadsInOneCall()
    {
        var (accessor, reader) = CreateAccessor(
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 1, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 2, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 3, 1)));

        await accessor.SubsAsync("doc-upload", CancellationToken.None);

        await reader.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs => refs.Count == 3),
            Arg.Any<CancellationToken>());
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task SubsAsync_ShouldOmitInstancesTheReaderDoesNotReturn()
    {
        var present = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
        var missing = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0");
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([
                Correlation(present, "doc-upload", new DateTime(2026, 1, 1)),
                Correlation(missing, "doc-upload", new DateTime(2026, 2, 1))
            ]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(
                (IReadOnlyList<RelatedInstanceSnapshot>)[Snapshot(present, "doc-upload")])));

        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        var result = await accessor.SubsAsync("doc-upload", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].InstanceId.ShouldBe(present);
    }

    [Fact]
    public async Task SubsAsync_ShouldThrow_WhenReaderFails()
    {
        var instance = Instance.Create(ParentId, "loan-application", "2.1.0");
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 1, 1))]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(
                Error.Failure("RELATED_READ", "compliance domain unreachable"))));

        var accessor = new RelatedInstanceAccessor(
            instance, reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        var exception = await Should.ThrowAsync<RelatedInstanceAccessException>(
            () => accessor.SubsAsync("doc-upload", CancellationToken.None));

        exception.Message.ShouldContain("compliance domain unreachable");
    }

    [Fact]
    public async Task SubKeysAsync_ShouldReturnDistinctKeysWithoutReadingData()
    {
        var (accessor, reader) = CreateAccessor(
            Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 2, 1)),
            Correlation(Guid.NewGuid(), "doc-upload", new DateTime(2026, 3, 1)));

        var keys = await accessor.SubKeysAsync(CancellationToken.None);

        keys.ShouldBe(["kyc-flow", "doc-upload"]);
        await reader.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default);
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task CorrelationList_ShouldBeLoadedOnlyOnce()
    {
        var correlationRepository = Substitute.For<IInstanceCorrelationRepository>();
        correlationRepository.GetByParentAsync(ParentId, Arg.Any<CancellationToken>())
            .Returns([Correlation(Guid.NewGuid(), "kyc-flow", new DateTime(2026, 1, 1))]);

        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var refs = call.Arg<IReadOnlyList<RelatedInstanceRef>>();
                IReadOnlyList<RelatedInstanceSnapshot> snapshots =
                    refs.Select(r => Snapshot(r.InstanceId, r.Flow)).ToList();
                return Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots));
            });

        var accessor = new RelatedInstanceAccessor(
            Instance.Create(ParentId, "loan-application", "2.1.0"),
            reader, correlationRepository, new RelatedAccessOptions(), NullLogger.Instance);

        await accessor.SubKeysAsync(CancellationToken.None);
        await accessor.SubAsync("kyc-flow", CancellationToken.None);
        await accessor.SubsAsync(null, CancellationToken.None);

        await correlationRepository.Received(1).GetByParentAsync(ParentId, Arg.Any<CancellationToken>());
    }
}
