using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Tasks.Executors;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Behavioural tests for <see cref="FanOutTaskExecutor"/>: one inner-task execution per item,
/// bounded parallelism, and exactly one joined output. Scaffolding lives in
/// <c>FanOutTestFixture.cs</c> and is shared with the join-policy and mapping suites.
/// </summary>
public sealed class FanOutTaskExecutorTests
{
    [Fact]
    public async Task Execute_RunsInnerTaskOncePerItem_AndProducesOneJoinedOutput()
    {
        var harness = new FanOutHarness(instanceData: new
        {
            documents = new[] { new { id = "doc-a" }, new { id = "doc-b" }, new { id = "doc-c" } }
        });

        var response = await harness.ExecuteAsync();

        response.IsSuccess.ShouldBeTrue();
        response.Value!.IsSuccess.ShouldBeTrue();

        // One inner execution per item, each on its own DI scope.
        harness.Engine.Calls.Count.ShouldBe(3);
        harness.ScopeFactory.ScopesCreated.ShouldBe(3);

        // Every item runs collect-only with a distinct journal identity and a pre-bound task —
        // this trio is what makes "N executions, one write" true.
        harness.Engine.Calls.Select(c => c.JournalTaskKey)
            .ShouldBe(["fan-out-docs#0", "fan-out-docs#1", "fan-out-docs#2"], ignoreOrder: true);
        harness.Engine.Calls.ShouldAllBe(c => c.SuppressDataApply);
        harness.Engine.Calls.ShouldAllBe(c => c.CaptureResponse);
        harness.Engine.Calls.ShouldAllBe(c => c.PreparedTask != null);

        // The single output: item results under the result key plus the batch summary.
        var results = harness.ItemResults(response);
        results.GetArrayLength().ShouldBe(3);
        results.EnumerateArray().Select(r => r.GetProperty("itemKey").GetString())
            .ShouldBe(["doc-a", "doc-b", "doc-c"]);
        results.EnumerateArray().ShouldAllBe(r => r.GetProperty("isSuccess").GetBoolean());

        var summary = harness.Summary(response);
        summary.GetProperty("total").GetInt32().ShouldBe(3);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(3);
        summary.GetProperty("failed").GetInt32().ShouldBe(0);
        summary.GetProperty("timedOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Execute_AlwaysReturnsResultsInItemIndexOrder_EvenWhenItemsSettleOutOfOrder()
    {
        // Item 0 is slowest, item 2 fastest — completion order is the reverse of index order.
        var harness = new FanOutHarness(instanceData: new
        {
            documents = new[] { new { id = "slow" }, new { id = "mid" }, new { id = "fast" } }
        });
        harness.Engine.DelayPerCall = order => TimeSpan.FromMilliseconds(60 - (order * 25));

        var response = await harness.ExecuteAsync();

        var results = harness.ItemResults(response);
        results.EnumerateArray().Select(r => r.GetProperty("itemKey").GetString())
            .ShouldBe(["slow", "mid", "fast"]);
        results.EnumerateArray().Select(r => r.GetProperty("index").GetInt32()).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task Execute_EmptyCollection_ExecutesNothing_AndSucceedsUnderAllSettled()
    {
        var harness = new FanOutHarness(instanceData: new { documents = Array.Empty<object>() });

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.ShouldBeEmpty();
        harness.ItemResults(response).GetArrayLength().ShouldBe(0);
        harness.Summary(response).GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Execute_EmptyCollection_StillFailsTheJoin_WhenPolicyNeedsASuccess()
    {
        // An empty batch is NOT unconditionally successful: it runs through the same join
        // evaluation as a non-empty one, and firstSuccess cannot be met by zero items.
        var harness = new FanOutHarness(
            instanceData: new { documents = Array.Empty<object>() },
            joinPolicy: FanOutJoinPolicy.FirstSuccess);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        harness.Engine.Calls.ShouldBeEmpty();
        // The output still lands so the flow can branch on it.
        harness.Summary(response).GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Execute_FailedJoin_StillCarriesTheResultSet()
    {
        var harness = new FanOutHarness(
            instanceData: new { documents = new[] { new { id = "ok" }, new { id = "bad" } } },
            joinPolicy: FanOutJoinPolicy.AllSettled);
        harness.Engine.FailOrders.Add(1);

        var response = await harness.ExecuteAsync();

        var summary = harness.Summary(response);
        summary.GetProperty("succeeded").GetInt32().ShouldBe(1);
        summary.GetProperty("failed").GetInt32().ShouldBe(1);
        harness.ItemResults(response).GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Execute_WhenCallerCancelsMidBatch_PropagatesCancellation_InsteadOfABusinessFailure()
    {
        // A torn-down transition must not come back as a 500 business failure that the error
        // boundary could then retry — the cancellation has to escape the executor.
        using var cts = new CancellationTokenSource();
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(3), maxDop: 1);
        harness.Engine.DelayPerCall = _ => TimeSpan.FromMilliseconds(50);
        harness.Engine.OnCallStarted = call =>
        {
            if (call.Order == 0)
            {
                cts.Cancel();
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(() => harness.ExecuteAsync(cts.Token));
    }

    [Fact]
    public async Task Execute_FailedItem_KeepsItsResponsePayload_AndReportsCodeAndDuration()
    {
        var harness = new FanOutHarness(
            instanceData: new { documents = new[] { new { id = "ok" }, new { id = "bad" } } });
        harness.Engine.FailOrders.Add(1);
        harness.Engine.DelayPerCall = order => order == 1 ? TimeSpan.FromMilliseconds(30) : TimeSpan.Zero;

        var response = await harness.ExecuteAsync();

        var failed = harness.ItemResults(response).EnumerateArray()
            .Single(r => !r.GetProperty("isSuccess").GetBoolean());

        // Under allSettled the author inspects the failed item's body to decide what to do —
        // the payload must survive the failure, not just the message.
        failed.GetProperty("data").GetProperty("order").GetInt32().ShouldBe(1);
        failed.GetProperty("errorMessage").GetString().ShouldNotBeNullOrWhiteSpace();
        // The inner failure's own code passes through; only fan-out's own causes get FanOut:* codes.
        failed.GetProperty("errorCode").GetString().ShouldBe("Item:Failed");
        failed.GetProperty("durationMs").GetInt64().ShouldBeGreaterThanOrEqualTo(20);
    }

    [Fact]
    public async Task Execute_WhenAnItemThrows_RecordsItAsFailed_WithoutLosingTheOtherItems()
    {
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(3));
        harness.Engine.ThrowOrders.Add(1);

        var response = await harness.ExecuteAsync();

        // One bad item must not take the batch's other outcomes down with it.
        response.Value!.IsSuccess.ShouldBeTrue(); // allSettled
        var results = harness.ItemResults(response);
        results.GetArrayLength().ShouldBe(3);

        var thrown = results.EnumerateArray().Single(r => r.GetProperty("index").GetInt32() == 1);
        thrown.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        thrown.GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.ItemFailed);
        thrown.GetProperty("errorMessage").GetString()!.ShouldContain("blew up");

        harness.Summary(response).GetProperty("succeeded").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Execute_WhenTheEngineReportsAnInfrastructureFailure_TheItemFailsWithThatCode()
    {
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(2));
        harness.Engine.InfrastructureFailureOrders.Add(0);

        var response = await harness.ExecuteAsync();

        var failed = harness.ItemResults(response).EnumerateArray()
            .Single(r => r.GetProperty("index").GetInt32() == 0);

        failed.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        failed.GetProperty("errorCode").GetString().ShouldBe("Engine:Unavailable");
        failed.GetProperty("errorMessage").GetString()!.ShouldContain("could not run item 0");
        // An infrastructure failure has no response, so no payload — but the batch still settles.
        harness.Summary(response).GetProperty("succeeded").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Execute_WhenInnerTaskIsItselfFanOut_Fails_WithoutRunningAnyItem()
    {
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(1),
            innerTemplate: FanOutHarness.CreateFanOutTask("inner-fan-out"));

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage.ShouldNotBeNull();
        response.Value!.ErrorMessage!.ShouldContain("nest", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WhenTheInnerTemplateCannotBeResolved_Fails_NamingTheTask()
    {
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(2));
        harness.TaskFactory
            .CreateExecutionTaskAsync(Arg.Any<IReference>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Fail(Error.NotFound("task.notfound", "no such component")));

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("process-document");
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WhenTheTaskHasNoInnerTaskReference_Fails()
    {
        // FanOutTask.Configure rejects a missing 'task', so this shape cannot come from JSON —
        // but the executor's guard is what protects every other construction path.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(1),
            taskOverride: FanOutHarness.CreateTaskWithoutItemTask());

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("'task' reference");
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WithNoItemsPathAndNoMapping_Fails_MentioningTheItemSource()
    {
        var harness = new FanOutHarness(itemsPath: null);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("item source", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WithBothItemsPathAndItemSelector_Fails_AsAmbiguous()
    {
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(1),
            mapping: new StubFanOutMapping { Items = [new { id = "from-selector" }] });

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("ambiguous", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Execute_WithItemSelector_FansOutOverTheSelectedValues()
    {
        var mapping = new StubFanOutMapping { Items = [new { id = "s-1" }, new { id = "s-2" }] };
        var harness = new FanOutHarness(itemsPath: null, mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(2);
        // The mapping binds every item, and the batch produces exactly ONE output-handler call.
        mapping.BoundItems.Count.ShouldBe(2);
        mapping.BoundItems.Select(i => i.ItemKey).ShouldBe(["s-1", "s-2"], ignoreOrder: true);
        mapping.OutputCalls.Count.ShouldBe(1);
        mapping.OutputCalls[0].Total.ShouldBe(2);
        mapping.OutputCalls[0].Succeeded.ShouldBe(2);
    }

    [Fact]
    public async Task Execute_NeverExceedsMaxDegreeOfParallelism()
    {
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(6), maxDop: 2);
        harness.Engine.DelayPerCall = _ => TimeSpan.FromMilliseconds(40);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(6);
        // Exactly 2: the ceiling holds, AND the batch genuinely overlapped rather than passing
        // by accidentally serialising.
        harness.Engine.PeakConcurrency.ShouldBe(2);
    }

    [Fact]
    public async Task Execute_ItemThatOverrunsItsOwnDeadline_IsClassifiedAsAnItemTimeout()
    {
        // Smoke test for the cancellation plumbing, not the timeout suite: it proves the fixture
        // can drive an item past its own deadline and that the classification names the item's
        // deadline rather than a batch cause. The exhaustive timeout/policy matrix is Task 8's.
        // One second is the floor — FanOutTask validates timeouts as whole seconds >= 1.
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(2),
            itemTimeoutSeconds: 1,
            batchTimeoutSeconds: 30);
        harness.Engine.DelayPerCall = order => order == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.Zero;

        var response = await harness.ExecuteAsync();

        var timedOutItem = harness.ItemResults(response).EnumerateArray()
            .Single(r => r.GetProperty("index").GetInt32() == 0);

        timedOutItem.GetProperty("isSuccess").GetBoolean().ShouldBeFalse();
        timedOutItem.GetProperty("errorCode").GetString().ShouldBe(FanOutErrorCodes.ItemTimeout);
        // One item's own deadline is not the batch's deadline.
        harness.Summary(response).GetProperty("timedOut").GetBoolean().ShouldBeFalse();
        harness.Summary(response).GetProperty("succeeded").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Execute_DiscardsItemBranchContexts_LeavingTheBatchContextUntouched()
    {
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(2));

        await harness.ExecuteAsync();

        // Each item ran on its own branch, and none of them was merged back: N items would
        // otherwise collide on the same inner task key in the batch context.
        harness.Engine.Calls.Select(c => c.Context).Distinct().Count().ShouldBe(2);
        harness.Engine.Calls.ShouldAllBe(c => !ReferenceEquals(c.Context, harness.ScriptContext));
        harness.ScriptContext.TaskResponse.ShouldBeEmpty();
    }
}
