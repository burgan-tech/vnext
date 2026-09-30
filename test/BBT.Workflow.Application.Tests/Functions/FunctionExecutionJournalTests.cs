using System;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionExecutionJournal"/> — the bounded, non-blocking producer queue
/// behind <c>IFunctionExecutionJournal</c>. Covers the happy enqueue path and every drop/shutdown
/// corner: a full queue sheds the newest record (observably), a completed queue rejects, and
/// <c>Record</c> never blocks nor throws.
/// </summary>
public class FunctionExecutionJournalTests
{
    private static FunctionExecutionRecord Sample(string key = "get-report") => new(
        Domain: "sample",
        FunctionKey: key,
        FunctionVersion: "1.0.0",
        Scope: TaskScope.Domain,
        Workflow: null,
        InstanceId: null,
        InvokedAt: DateTime.UtcNow,
        DurationMs: 10,
        Succeeded: true,
        StatusCode: 200,
        ErrorCode: null,
        FromCache: false);

    [Fact]
    public void Record_EnqueuesRecord_AndReaderReadsItBack()
    {
        var journal = new FunctionExecutionJournal();
        var record = Sample();

        var accepted = journal.Record(record);

        accepted.ShouldBeTrue();
        journal.Reader.TryRead(out var read).ShouldBeTrue();
        read.ShouldBeSameAs(record);
        journal.DroppedCount.ShouldBe(0);
    }

    [Fact]
    public void Record_WhenQueueFull_DropsNewestReturnsFalse_AndCountsTheDrop()
    {
        // Capacity 1: the first record fills the queue, the second is dropped (Wait mode + TryWrite).
        var journal = new FunctionExecutionJournal(capacity: 1);

        journal.Record(Sample("first")).ShouldBeTrue();
        journal.Record(Sample("second")).ShouldBeFalse();

        journal.DroppedCount.ShouldBe(1);
        // The record that stayed is the FIRST — the newest was the one dropped.
        journal.Reader.TryRead(out var read).ShouldBeTrue();
        read!.FunctionKey.ShouldBe("first");
    }

    [Fact]
    public void DroppedCount_AccumulatesAcrossManyDrops()
    {
        var journal = new FunctionExecutionJournal(capacity: 1);
        journal.Record(Sample());

        for (var i = 0; i < 5; i++)
        {
            journal.Record(Sample()).ShouldBeFalse();
        }

        journal.DroppedCount.ShouldBe(5);
    }

    [Fact]
    public void Record_AfterComplete_ReturnsFalse_AndDoesNotThrow()
    {
        var journal = new FunctionExecutionJournal();
        journal.Complete();

        var accepted = Should.NotThrow(() => journal.Record(Sample()));

        accepted.ShouldBeFalse();
    }

    [Fact]
    public void InvalidCapacity_FallsBackToDefault()
    {
        // A non-positive capacity must not throw — it falls back to the default bound.
        var journal = Should.NotThrow(() => new FunctionExecutionJournal(capacity: 0));

        journal.Record(Sample()).ShouldBeTrue();
    }
}
