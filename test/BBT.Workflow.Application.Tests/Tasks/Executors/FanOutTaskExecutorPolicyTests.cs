using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Join-policy BEHAVIOUR of <see cref="FanOutTaskExecutor"/>: what the executor actually DOES while
/// items are in flight — the early-stop machinery, the two deadlines and their classification, and
/// how partial failure reaches the output.
/// </summary>
/// <remarks>
/// <para>
/// Complementary to <c>FanOutJoinEvaluatorTests</c>, which pins the PURE decision table
/// (settled results + policy → success/failure). A pure function cannot observe an early stop, a
/// cancelled sibling or a deadline, so none of that is testable there and none of it is restated
/// here.
/// </para>
/// <para>
/// <strong>No assertion in this file reads the clock.</strong> Concurrency tests that assert on
/// elapsed time are flaky by construction on a loaded CI box. Early stop is proven by
/// <see cref="RecordingTaskExecutionEngine.CompletedCalls"/> (work avoided) and by the cancelled
/// items' error codes; a deadline is proven by the code the item settled with. Timeouts cost real
/// seconds — <c>FanOutTask</c> validates them as whole seconds >= 1 — so the two tests that need
/// one are the minimum that covers both classification outcomes, and every deadline in them is
/// spaced a full second from the next.
/// </para>
/// </remarks>
public sealed class FanOutTaskExecutorPolicyTests
{
    /// <summary>A long delay, used for items a test expects to be cut short before it ever elapses.</summary>
    private static readonly TimeSpan NeverFinishes = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task All_OnTheFirstFailure_CancelsTheRemainingItems_InsteadOfLettingThemRun()
    {
        // Item 0 fails immediately; items 1-3 would each take half a minute. Under 'all' the
        // verdict can no longer change once one item has failed, so the rest must be cancelled.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(4),
            joinPolicy: FanOutJoinPolicy.All,
            maxDop: 4);
        harness.Engine.FailOrders.Add(0);
        harness.Engine.DelayPerCall = order => order == 0 ? TimeSpan.Zero : NeverFinishes;

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();

        // The whole point of early stop: the slow items did NOT run to completion. Exactly one
        // item's work reached its end — the one that decided the batch.
        harness.Engine.CompletedCalls.ShouldBe(1);

        // And they were cancelled by the JOIN POLICY, not merely left unstarted or failed by
        // something else — proving the early-stop signal is what fired.
        foreach (var index in new[] { 1, 2, 3 })
        {
            var cancelled = ItemAt(harness, response, index);
            cancelled.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
            cancelled.GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.ItemCancelled);
            cancelled.GetProperty("errorMessage").GetString()!.ShouldContain("early stop");
        }

        var summary = harness.Summary(response);
        summary.GetProperty("total").GetInt32().ShouldBe(4);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(0);
        summary.GetProperty("failed").GetInt32().ShouldBe(4);

        // An early stop is NOT a timeout. Deriving TimedOut from "the batch token was cancelled"
        // would report true here and make the two cancellation causes indistinguishable.
        summary.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task FirstSuccess_OnTheFirstSuccess_CancelsTheRemainingItems_AndSucceeds()
    {
        // The mirror image: one fast success among three that would never finish. 'firstSuccess'
        // is a redundant-source policy, so the redundant work must actually be abandoned.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(4),
            joinPolicy: FanOutJoinPolicy.FirstSuccess,
            maxDop: 4);
        harness.Engine.DelayPerCall = order => order == 0 ? TimeSpan.Zero : NeverFinishes;

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.CompletedCalls.ShouldBe(1);

        ItemAt(harness, response, 0).GetProperty("isSuccess").GetBoolean().ShouldBeTrue();
        foreach (var index in new[] { 1, 2, 3 })
        {
            ItemAt(harness, response, index).GetProperty("errorCode").GetString()
                .ShouldBe(FanOutErrorCodes.ItemCancelled);
        }

        var summary = harness.Summary(response);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(3);
        summary.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Quorum_WhenMinSuccessIsMet_Succeeds_WithTheCountsInTheSummary()
    {
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(5),
            joinPolicy: FanOutJoinPolicy.Quorum,
            minSuccess: 3);
        harness.Engine.FailOrders.Add(3);
        harness.Engine.FailOrders.Add(4);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        response.Value!.ErrorMessage.ShouldBeNull();

        // Quorum needs every outcome to count them, so it must NOT stop early even after the
        // threshold is mathematically settled.
        harness.Engine.CompletedCalls.ShouldBe(5);

        var summary = harness.Summary(response);
        summary.GetProperty("total").GetInt32().ShouldBe(5);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(3);
        summary.GetProperty("failed").GetInt32().ShouldBe(2);
        summary.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Quorum_WhenMinSuccessIsNotMet_Fails_ButStillLandsTheCounts()
    {
        // Same batch, threshold raised by one — the only difference between this and the previous
        // test, so a regression in the comparison cannot pass both.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(5),
            joinPolicy: FanOutJoinPolicy.Quorum,
            minSuccess: 4);
        harness.Engine.FailOrders.Add(3);
        harness.Engine.FailOrders.Add(4);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("quorum");

        // A failed join still carries its data, so the flow can branch on WHICH items failed.
        var summary = harness.Summary(response);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(3);
        summary.GetProperty("failed").GetInt32().ShouldBe(2);
        harness.ItemResults(response).GetArrayLength().ShouldBe(5);
    }

    [Fact]
    public async Task AllSettled_WhenEveryItemFails_StillSucceeds_AndEveryFailureReachesTheOutput()
    {
        // Three different failure shapes at once: a business failure, a thrown inner task, and an
        // engine-level (Result) failure. Under allSettled partial — here total — failure is DATA,
        // and the author branches on {resultKey}Summary.failed, so every code and message has to
        // survive into the result set.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(3),
            joinPolicy: FanOutJoinPolicy.AllSettled);
        harness.Engine.FailOrders.Add(0);
        harness.Engine.ThrowOrders.Add(1);
        harness.Engine.InfrastructureFailureOrders.Add(2);

        var response = await harness.ExecuteAsync();

        response.IsSuccess.ShouldBeTrue();
        response.Value!.IsSuccess.ShouldBeTrue();
        response.Value!.ErrorMessage.ShouldBeNull();

        // allSettled never stops early: every item is needed for the summary the author reads.
        harness.Engine.CompletedCalls.ShouldBe(3);

        var summary = harness.Summary(response);
        summary.GetProperty("total").GetInt32().ShouldBe(3);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(0);
        summary.GetProperty("failed").GetInt32().ShouldBe(3);
        summary.GetProperty("timedOut").GetBoolean().ShouldBeFalse();

        var results = harness.ItemResults(response);
        results.GetArrayLength().ShouldBe(3);
        results.EnumerateArray().ShouldAllBe(r => !r.GetProperty("isSuccess").GetBoolean());
        results.EnumerateArray()
            .ShouldAllBe(r => !string.IsNullOrWhiteSpace(r.GetProperty("errorMessage").GetString()));

        // Codes are branchable and distinct per cause: the inner task's own code passes through,
        // a throw becomes ItemFailed, the engine's Result error keeps its own code.
        ItemAt(harness, response, 0).GetProperty("errorCode").GetString().ShouldBe("Item:Failed");
        ItemAt(harness, response, 1).GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.ItemFailed);
        ItemAt(harness, response, 2).GetProperty("errorCode").GetString().ShouldBe("Engine:Unavailable");
    }

    [Fact]
    public async Task BatchTimeout_UnderAll_FailsTheTask_AndSaysTheBatchTimedOut()
    {
        // Timeline (maxDop 1, so item 1 queues behind item 0):
        //   t=0   item 0 starts, its own window closes at t=2
        //   t=1   item 0 succeeds; item 1 starts, its own window would close at t=3
        //   t=2   the BATCH deadline fires — a full second before item 1's own deadline
        // Item 1 is therefore cut short by the batch, not by itself. This staggering is the only
        // way to reach the batch deadline first: FanOutTask rejects itemTimeout > batchTimeout,
        // so an item that starts at t=0 always blows its own deadline first.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(2),
            joinPolicy: FanOutJoinPolicy.All,
            maxDop: 1,
            itemTimeoutSeconds: 2,
            batchTimeoutSeconds: 2);
        harness.Engine.DelayPerCall = order =>
            order == 0 ? TimeSpan.FromSeconds(1) : NeverFinishes;

        var response = await harness.ExecuteAsync();

        var cutShort = ItemAt(harness, response, 1);
        cutShort.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        cutShort.GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.BatchTimeout);

        var summary = harness.Summary(response);
        summary.GetProperty("timedOut").GetBoolean().ShouldBeTrue();
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);

        // 'all' treats a timed-out batch as a distinct failure from "some item failed", and says so.
        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("timed out");
    }

    [Fact]
    public async Task ItemTimeoutAndBatchTimeout_AreClassifiedApart_AndAllSettledStillSucceeds()
    {
        // Timeline (maxDop 1):
        //   t=0   item 0 starts, own window closes at t=2, batch deadline at t=3
        //   t=2   item 0 blows its OWN deadline -> ItemTimeout (batch still a second away)
        //   t=2   item 1 starts, own window would close at t=4
        //   t=3   the BATCH deadline fires, a second before item 1's own -> BatchTimeout
        // Every boundary is a full second from the next, so no assertion depends on scheduling luck.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(2),
            joinPolicy: FanOutJoinPolicy.AllSettled,
            maxDop: 1,
            itemTimeoutSeconds: 2,
            batchTimeoutSeconds: 3);
        harness.Engine.DelayPerCall = _ => NeverFinishes;

        var response = await harness.ExecuteAsync();

        // The item that misbehaved is named as the misbehaving one; the collateral casualty is not.
        var ownDeadline = ItemAt(harness, response, 0);
        ownDeadline.GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.ItemTimeout);
        ownDeadline.GetProperty("errorMessage").GetString()!.ShouldContain("item timeout");

        var batchDeadline = ItemAt(harness, response, 1);
        batchDeadline.GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.BatchTimeout);
        batchDeadline.GetProperty("errorMessage").GetString()!.ShouldContain("batch timeout");

        // Neither item ran to completion, and no work was wasted pretending otherwise.
        harness.Engine.CompletedCalls.ShouldBe(0);

        var summary = harness.Summary(response);
        summary.GetProperty("failed").GetInt32().ShouldBe(2);
        // TimedOut is derived from an item genuinely carrying the BATCH code — an item timeout
        // alone must not raise it, which is what the previous suite's single-item case pins.
        summary.GetProperty("timedOut").GetBoolean().ShouldBeTrue();

        // allSettled succeeds even on a timed-out batch: that is the whole point of the policy.
        response.Value!.IsSuccess.ShouldBeTrue();
        response.Value!.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task EarlyStop_ItemsAlreadyInsideTheEngine_CarryItemCancelled_NotTheInnerExceptionCode()
    {
        // The regression this pins: the task engine absorbs an item's TaskCanceledException and hands
        // back `Task:Unknown:{itemTaskKey}:TaskCanceledException`. Accepted verbatim, one batch
        // reported TWO codes for one cause — the item cancelled while still queueing behind the
        // degree gate got FanOut:ItemCancelled, its siblings already inside the engine got the
        // exception name. maxDop 3 over 5 items reproduces exactly that split: items 1-2 are in
        // flight when item 0 decides the batch, items 3-4 are still queueing.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(5),
            joinPolicy: FanOutJoinPolicy.FirstSuccess,
            maxDop: 3);
        harness.Engine.DelayPerCall = order => order == 0 ? TimeSpan.Zero : NeverFinishes;

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        ItemAt(harness, response, 0).GetProperty("isSuccess").GetBoolean().ShouldBeTrue();

        // Every cancelled item — in flight or still queueing — reports the SAME documented code.
        // FanOutErrorCodes values are public contract; authors branch on them.
        foreach (var index in new[] { 1, 2, 3, 4 })
        {
            var cancelled = ItemAt(harness, response, index);
            var code = cancelled.GetProperty("errorCode").GetString();

            cancelled.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
            code.ShouldBe(FanOutErrorCodes.ItemCancelled, $"item {index} leaked a non-contract code");

            // Stated separately from the equality above so a future leak reads as what it is: the
            // inner task's exception name, and the inner task's KEY, escaping into the contract.
            code!.ShouldNotContain(nameof(TaskCanceledException));
            code!.ShouldNotContain("process-document");
        }
    }

    [Fact]
    public async Task ItemFailingOnItsOwnTerms_WhileTheBatchIsStopping_KeepsItsOwnErrorCode()
    {
        // The other half of the re-attribution rule. Item 1 fails immediately, which under 'all'
        // stops the batch; item 0 is already in flight, ignores the cancellation, and settles with
        // its OWN business failure a moment later. Its window token is cancelled by then, so a
        // token-only rule would relabel a genuine 502 as "cancelled by the join policy" and hide the
        // item that actually misbehaved.
        //
        // Item 0 is the slow one on purpose: the batch's items are started by enumerating a
        // projection, so item 0 runs inline as far as its first real await — it is inside the engine
        // before item 1 exists. The reverse arrangement races item 1 against its own concurrency
        // slot and intermittently never enters the engine at all.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(2),
            joinPolicy: FanOutJoinPolicy.All,
            maxDop: 2);
        harness.Engine.FailOrders.Add(0);
        harness.Engine.FailOrders.Add(1);
        harness.Engine.IgnoreCancellationOrders.Add(0);
        harness.Engine.DelayPerCall = order => order == 0 ? TimeSpan.FromMilliseconds(120) : TimeSpan.Zero;

        var response = await harness.ExecuteAsync();

        // Both ran to their own end; neither was cut short.
        harness.Engine.CompletedCalls.ShouldBe(2);

        foreach (var index in new[] { 0, 1 })
        {
            var failed = ItemAt(harness, response, index);
            failed.GetProperty("errorCode").GetString().ShouldBe("Item:Failed");
            // The payload an author inspects survives, which a cancellation classification discards.
            failed.GetProperty("data").GetProperty("order").GetInt32().ShouldBe(index);
        }
    }

    /// <summary>The default packaging's result entry for one item index.</summary>
    private static JsonElement ItemAt(FanOutHarness harness, Result<StandardTaskResponse> response, int index) =>
        harness.ItemResults(response).EnumerateArray()
            .Single(r => r.GetProperty("index").GetInt32() == index);
}
