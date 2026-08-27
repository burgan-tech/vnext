using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.Telemetry;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// The observability CONTRACT of <see cref="FanOutTaskExecutor"/>: one batch recording per batch,
/// carrying counters that agree with the batch's own output.
/// </summary>
/// <remarks>
/// <para>
/// Log lines are asserted only where a specific structured FIELD is the contract — the item alias,
/// which exists for no other purpose than to be read here. Those assertions go through
/// <c>FanOutHarness.LoggedFields</c>, which reads the entry's state object, never its rendered
/// message: the wording of a message template is not a contract and must stay free to change.
/// </para>
/// <para>
/// Everything else is pinned through metrics, which are a typed interface call rather than a
/// formatted string and are therefore the cheaper and more stable assertion.
/// </para>
/// </remarks>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class FanOutTaskExecutorObservabilityTests
{
    /// <summary>EventId of <c>WorkflowLogs.FanOutBatchStarted</c>.</summary>
    private const int FanOutBatchStartedEventId = 10150;

    /// <summary>EventId of <c>WorkflowLogs.FanOutItemFailed</c>.</summary>
    private const int FanOutItemFailedEventId = 10151;

    /// <summary>
    /// The per-item failure log must carry the failure MESSAGE, not just its code.
    /// </summary>
    /// <remarks>
    /// The message is otherwise attached only to the item span, and item spans are emitted at
    /// Verbose tracing detail only — so at the default level the reason an item failed was
    /// unrecoverable. That cost a real incident: a fan-out over a SubProcess launch failed every
    /// item with nothing in the logs but an error code.
    /// </remarks>
    [Fact]
    public async Task AFailedItem_LogsTheFailureMessage_NotJustItsCode()
    {
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(3),
            joinPolicy: FanOutJoinPolicy.AllSettled,
            maxDop: 1);
        harness.Engine.FailOrders.Add(1);

        await harness.ExecuteAsync();

        var fields = harness.LoggedFields(FanOutItemFailedEventId);
        fields["ErrorMessage"].ShouldBe("item 1 failed");

        // Its own structured field: a backend must be able to facet on the code and read the
        // message as free text, so the message is never spliced into ErrorCode.
        fields["ErrorCode"].ShouldBe("Item:Failed");
        fields["ItemIndex"].ShouldBe(1);
    }







    [Fact]
    public async Task TheItemAlias_ReachesTheBatchStartedLog_AsItsOwnStructuredField()
    {
        // The whole reason itemAlias exists. Before this, it was parsed, cloned and reset but read
        // by nothing — a published config field that documented a purpose it did not have.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(12),
            itemAlias: "document");

        await harness.ExecuteAsync();

        var fields = harness.LoggedFields(FanOutBatchStartedEventId);
        fields["ItemAlias"].ShouldBe("document");

        // Its own field, not spliced into the count: a backend has to be able to facet on the
        // alias and read the count as a number.
        fields["ItemCount"].ShouldBe(12);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AnAbsentOrBlankItemAlias_FallsBackToANeutralLabel_RatherThanLoggingNothing(
        string? authoredAlias)
    {
        // "Items=3 ''" is worse than no alias at all, and an author may well write "itemAlias": ""
        // — so blank is treated as absent rather than faithfully echoed.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(3),
            itemAlias: authoredAlias);

        await harness.ExecuteAsync();

        harness.LoggedFields(FanOutBatchStartedEventId)["ItemAlias"].ShouldBe("item");
    }

    [Fact]
    public async Task TheItemAlias_IsTaggedOnEveryItemSpan_AlongsideTheItemKeyAndIndex()
    {
        using var trace = new FanOutTraceCapture();

        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(3),
            itemAlias: "document",
            maxDop: 1);

        await harness.ExecuteAsync();

        var itemSpans = trace.ItemSpans();
        itemSpans.Count.ShouldBe(3);

        foreach (var span in itemSpans)
        {
            span.GetTagItem("vnext.fanout.item.alias").ShouldBe("document");
        }

        // Alias alone cannot identify a straggler — it is the same for every item by definition.
        // It is only useful next to the per-item identity, so that pairing is pinned too.
        itemSpans
            .Select(span => (int)span.GetTagItem("vnext.fanout.item.index")!)
            .ShouldBe(new[] { 0, 1, 2 }, ignoreOrder: true);
        itemSpans
            .Select(span => span.GetTagItem("vnext.fanout.item.key"))
            .ShouldAllBe(key => key != null);
    }

    [Fact]
    public void TheTaskActivitySourceName_MatchesTheLiteralTheTraceCaptureListensOn()
    {
        // FanOutTraceCapture cannot reference the helper's ActivitySource from inside its listener
        // predicate without poisoning the type, so it duplicates the name as a literal. This is the
        // guard that stops the copy from drifting and silently capturing nothing.
        TaskExecutionActivityHelper.ActivitySource.Name.ShouldBe("BBT.Workflow.Tasks");
    }

    [Fact]
    public void TheBulkhead_ReportsItsConfiguredCapacity_AlongsideItsActiveCount()
    {
        // Capacity is the denominator the saturation warning is read against; without it "Active=8"
        // says nothing about whether the bulkhead is the bottleneck.
        var limiter = new FanOutConcurrencyLimiter(
            Microsoft.Extensions.Options.Options.Create(new FanOutOptions { MaxConcurrentItems = 3 }));

        limiter.Capacity.ShouldBe(3);
        limiter.ActiveCount.ShouldBe(0);
    }
}

/// <summary>
/// Puts the executor's item spans within reach of an assertion: switches the tracing runtime to
/// Verbose (nothing below it creates task spans at all) and records every stopped activity from
/// the task <c>ActivitySource</c>.
/// </summary>
/// <remarks>
/// <para>
/// Records on STOP, not start: the tags that matter — the alias, the queue wait, the error status —
/// are set over the item's lifetime, so a span captured at creation would be asserted half-built.
/// </para>
/// <para>
/// Capture is scoped to a root activity this type starts, and <see cref="ItemSpans"/> returns only
/// spans sharing its trace. Both the listener and the detail level are PROCESS-WIDE: the moment
/// this switches tracing to Verbose, every other fan-out test running in parallel starts emitting
/// item spans onto the same source, and an unscoped capture counts theirs too. The collection this
/// type's users belong to serializes them against other tracing tests, not against the rest of the
/// suite.
/// </para>
/// </remarks>
internal sealed class FanOutTraceCapture : IDisposable
{
    /// <summary>
    /// Name of <c>TaskExecutionActivityHelper.ActivitySource</c>, duplicated as a literal on
    /// purpose — see the listener predicate below.
    /// </summary>
    private const string TaskSourceName = "BBT.Workflow.Tasks";

    private readonly ActivityListener _listener;
    private readonly AetherTracingDetailLevel _originalLevel = AetherTracingRuntime.DetailLevel;
    private readonly ConcurrentBag<Activity> _stopped = new();
    private readonly Activity _root;

    public FanOutTraceCapture()
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Verbose);

        // Ambient root, so every item span started on this test's async context lands in one trace
        // that ItemSpans can filter on. Activity.Current is AsyncLocal, so a sibling test class
        // running concurrently gets its own root and never bleeds into this one.
        _root = new Activity("fanout-trace-capture");
        _root.SetIdFormat(ActivityIdFormat.W3C);
        _root.Start();

        _listener = new ActivityListener
        {
            // The source name is a LITERAL, never TaskExecutionActivityHelper.ActivitySource.Name.
            // AddActivityListener runs this predicate against every existing source while holding
            // its own lock, so reading that static readonly field from inside it triggers the
            // helper's static initializer — which constructs an ActivitySource — re-entrantly. That
            // throws, and a failed static initializer is permanent: the type stays poisoned for the
            // rest of the process and every later test touching a task span dies with it.
            ShouldListenTo = source => source.Name == TaskSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// The per-item spans, separated from the executor's other phase spans (PrepareInput, Invoke,
    /// ProcessOutput) which share the same source.
    /// </summary>
    /// <remarks>
    /// Matched on <see cref="Activity.OperationName"/>, which is fixed at creation — deliberately
    /// NOT on <c>DisplayName</c>, which the executor rewrites per item and which a test therefore
    /// has no business depending on to FIND the span it wants to inspect.
    /// </remarks>
    public IReadOnlyList<Activity> ItemSpans() => _stopped
        .Where(activity => activity.OperationName == TaskExecutionActivityHelper.OperationFanOutItem
                           && activity.TraceId == _root.TraceId)
        .ToList();

    public void Dispose()
    {
        AetherTracingRuntime.Configure(_originalLevel);
        _listener.Dispose();
        _root.Stop();
        _root.Dispose();
        Activity.Current = null;
    }
}
