using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using BBT.Workflow.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionExecutionJournalWriter"/> — the background consumer that drains the
/// journal queue and bulk-inserts on its own DI scope. Covers the happy batch flush (and record→entity
/// mapping, including the caller identity), batch chunking, an empty queue, a persistence failure being
/// swallowed so the loop survives, and the shutdown drain.
/// </summary>
public sealed class FunctionExecutionJournalWriterTests
{
    private readonly IFunctionExecutionRepository _repository = Substitute.For<IFunctionExecutionRepository>();
    private readonly IUnitOfWorkManager _uowManager = Substitute.For<IUnitOfWorkManager>();
    private readonly IGuidGenerator _guidGenerator = Substitute.For<IGuidGenerator>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly List<FunctionExecution> _inserted = new();

    public FunctionExecutionJournalWriterTests()
    {
        var uow = Substitute.For<IUnitOfWork>();
        uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);

        _guidGenerator.Create().Returns(_ => Guid.NewGuid());

        _repository
            .InsertBatchAsync(
                Arg.Do<IReadOnlyCollection<FunctionExecution>>(b => _inserted.AddRange(b)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IUnitOfWorkManager)).Returns(_uowManager);
        provider.GetService(typeof(IFunctionExecutionRepository)).Returns(_repository);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory.CreateScope().Returns(scope);
    }

    private FunctionExecutionJournalWriter NewWriter(FunctionExecutionJournal journal) =>
        new(journal, _scopeFactory, _guidGenerator,
            NullLogger<FunctionExecutionJournalWriter>.Instance);

    private static FunctionExecutionRecord Record(string key, string? invokedBy = null) => new(
        Domain: "sample",
        FunctionKey: key,
        FunctionVersion: "1.0.0",
        Scope: TaskScope.Instance,
        Workflow: "north-star",
        InstanceId: Guid.NewGuid(),
        InvokedAt: DateTime.UtcNow,
        DurationMs: 12.5,
        Succeeded: true,
        StatusCode: 200,
        ErrorCode: null,
        FromCache: false,
        InvokedBy: invokedBy,
        InvokedByBehalfOf: "behalf",
        TraceId: "0af7651916cd43dd8448eb211c80319c");

    /// <summary>
    /// Enqueues, completes the queue, then runs the writer to completion. Completing before start makes
    /// the drain fully deterministic: WaitToReadAsync yields the queued records, then reports completion.
    /// </summary>
    private async Task RunToCompletionAsync(FunctionExecutionJournal journal, FunctionExecutionJournalWriter writer)
    {
        journal.Complete();
        await writer.StartAsync(CancellationToken.None);
        await writer.ExecuteTask!;
    }

    [Fact]
    public async Task Drains_MapsEachRecordToEntity_AndBulkInserts()
    {
        var journal = new FunctionExecutionJournal();
        journal.Record(Record("get-report", invokedBy: "alice"));
        journal.Record(Record("get-summary", invokedBy: "bob"));

        await RunToCompletionAsync(journal, NewWriter(journal));

        _inserted.Count.ShouldBe(2);
        // Caller identity is carried onto the persisted entity (InvokedBy → CreatedBy).
        var report = _inserted.Single(e => e.FunctionKey == "get-report");
        report.CreatedBy.ShouldBe("alice");
        report.CreatedByBehalfOf.ShouldBe("behalf");
        report.Scope.ShouldBe("I");
        report.Workflow.ShouldBe("north-star");
        report.TraceId.ShouldBe("0af7651916cd43dd8448eb211c80319c");
        report.CreatedAt.ShouldBe(report.InvokedAt);
    }

    [Fact]
    public async Task EmptyQueue_DoesNotInsert()
    {
        var journal = new FunctionExecutionJournal();

        await RunToCompletionAsync(journal, NewWriter(journal));

        await _repository.DidNotReceive().InsertBatchAsync(
            Arg.Any<IReadOnlyCollection<FunctionExecution>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MoreThanBatchSize_FlushesInMultipleBatches()
    {
        var journal = new FunctionExecutionJournal();
        var total = FunctionExecutionJournalWriter.BatchSize + 1;
        for (var i = 0; i < total; i++)
        {
            journal.Record(Record($"fn-{i}"));
        }

        await RunToCompletionAsync(journal, NewWriter(journal));

        // All rows persisted, and no single batch exceeded the cap.
        _inserted.Count.ShouldBe(total);
        await _repository.Received(2).InsertBatchAsync(
            Arg.Is<IReadOnlyCollection<FunctionExecution>>(b => b.Count <= FunctionExecutionJournalWriter.BatchSize),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenABatchFails_TheLoopSurvives_AndLaterBatchesStillFlush()
    {
        // First flush throws, later flushes succeed. Proves the loop keeps iterating after swallowing a
        // failure — not merely that it happened to end right after the one exception it saw. Uses a local
        // capture list (only successful flushes) rather than the fixture's _inserted, whose Arg.Do fires
        // on the throwing call too.
        var calls = 0;
        var persisted = new System.Collections.Generic.List<FunctionExecution>();
        _repository
            .InsertBatchAsync(Arg.Any<IReadOnlyCollection<FunctionExecution>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException("db down");
                }

                persisted.AddRange((IReadOnlyCollection<FunctionExecution>)ci[0]);
                return Task.CompletedTask;
            });

        var journal = new FunctionExecutionJournal();
        // BatchSize + 1 → two batches: the first throws, the second must still be flushed.
        var total = FunctionExecutionJournalWriter.BatchSize + 1;
        for (var i = 0; i < total; i++)
        {
            journal.Record(Record($"fn-{i}"));
        }

        var writer = NewWriter(journal);
        await Should.NotThrowAsync(async () => await RunToCompletionAsync(journal, writer));

        writer.ExecuteTask!.IsFaulted.ShouldBeFalse();
        calls.ShouldBe(2);                 // the loop attempted the second batch after swallowing the first
        persisted.Count.ShouldBe(1);       // and the second batch (the single trailing record) persisted
    }

    [Fact]
    public async Task Shutdown_DrainsAllRemaining_EvenAboveBatchSize()
    {
        // Regression guard: the final drain must flush EVERYTHING queued at shutdown, not just one
        // BatchSize-capped batch. StopAsync cancels the stopping token, so the drain cannot rely on the
        // main loop — it must loop until the queue is empty.
        var journal = new FunctionExecutionJournal();
        var writer = NewWriter(journal);

        await writer.StartAsync(CancellationToken.None);
        var total = (FunctionExecutionJournalWriter.BatchSize * 2) + 50; // well above one batch
        for (var i = 0; i < total; i++)
        {
            journal.Record(Record($"fn-{i}"));
        }

        // StopAsync completes the queue and awaits the full drain — nothing already enqueued is lost.
        await writer.StopAsync(CancellationToken.None);

        _inserted.Count.ShouldBe(total);
    }
}
