using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Executors;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// <see cref="IFanOutMapping"/> integration for <see cref="FanOutTaskExecutor"/>: how a workflow
/// author's mapping is actually driven — one isolated binding per item, one selector, and exactly
/// one output.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The invariant this suite exists to protect is the single write point.</strong> The
/// contract's claim — "N parallel item handlers are safe instead of a data race" — rests on three
/// mechanical facts that nothing else pins: each handler gets its OWN clone of the inner task, each
/// runs on its OWN discarded branch context, and the batch merges data exactly ONCE, in the output
/// handler. Break any one and the design's safety argument is gone while every join-policy test
/// still passes.
/// </para>
/// <para>
/// Deliberately NOT restated here: the default (mapping-less) output packaging and the join
/// decision table (<c>FanOutTaskExecutorTests</c> / <c>FanOutTaskExecutorPolicyTests</c>), and the
/// item-key extraction rules per value shape (<c>FanOutItemsResolverTests</c> covers those
/// exhaustively at unit level). What is tested here is the WIRING between the mapping and the
/// executor. Note that when a mapping OVERRIDES <c>OutputHandler</c> the default packaging is
/// replaced, so per-item outcomes are asserted through <c>OutputCalls[0]</c> — what the author's
/// handler actually sees — rather than through the harness's <c>ItemResults</c> helper. A mapping
/// that leaves <c>OutputHandler</c> unoverridden keeps the default shape, which is its own test.
/// </para>
/// </remarks>
public sealed class FanOutTaskExecutorMappingTests
{
    [Fact]
    public async Task ItemInputHandler_GetsItsOwnCloneOfTheInnerTask_NeverTheSharedTemplate()
    {
        var mapping = new StubFanOutMapping();
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(3), mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        mapping.Bindings.Count.ShouldBe(3);

        // Three DIFFERENT task objects. Binding mutates the task in place, in parallel, so handing
        // out one shared instance would let concurrent items overwrite each other's input — the
        // exact corruption the clone-per-item rule exists to prevent.
        mapping.Bindings.Select(b => b.Task).Distinct(ReferenceEqualityComparer.Instance)
            .Count().ShouldBe(3);

        // And none of them is the template the factory handed the executor: mutating that would
        // additionally leak this batch's binding into the component cache's copy.
        mapping.Bindings.ShouldAllBe(b => !ReferenceEquals(b.Task, harness.Template));

        // A clone, not a foreign object: same type and same configured identity as the template.
        mapping.Bindings.ShouldAllBe(b => b.Task is HttpTask);
        mapping.Bindings.ShouldAllBe(b => b.Task.Key == harness.Template.Key);
    }

    [Fact]
    public async Task ItemInputHandler_RunsOnAnIsolatedBranchContext_WhileOutputHandlerRunsOnTheBatchContext()
    {
        var mapping = new StubFanOutMapping();
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(3), mapping: mapping);

        await harness.ExecuteAsync();

        // One branch per item, and never the batch context: a handler writing to the batch context
        // from three threads is precisely the race the branch isolation removes.
        mapping.Bindings.Select(b => b.Context).Distinct(ReferenceEqualityComparer.Instance)
            .Count().ShouldBe(3);
        mapping.Bindings.ShouldAllBe(b => !ReferenceEquals(b.Context, harness.ScriptContext));

        // The branch the handler wrote to is the SAME one the engine then executes on — otherwise
        // the handler's context-level binding (SetBody and friends) would be silently discarded.
        foreach (var binding in mapping.Bindings)
        {
            var call = harness.Engine.Calls.Single(c => c.Order == binding.Item.Index);
            call.Context.ShouldBeSameAs(binding.Context);
        }

        // The single write point runs on the BATCH context, not a branch — a branch is discarded,
        // so an output handler on one could never reach instance data.
        mapping.OutputContexts.Count.ShouldBe(1);
        mapping.OutputContexts[0].ShouldBeSameAs(harness.ScriptContext);

        // The selector, likewise, runs on the batch context before any branch exists.
        mapping.SelectorContexts.ShouldAllBe(c => ReferenceEquals(c, harness.ScriptContext));
    }

    [Fact]
    public async Task ItemInputHandler_MutationsToTheClonedTask_ReachTheEngineForThatItem()
    {
        // Without this, binding is decorative: the handler could mutate a clone the executor then
        // throws away, and every item would hit the template's endpoint.
        var mapping = new StubFanOutMapping
        {
            BindItem = (task, _, item) =>
            {
                var http = (HttpTask)task;
                http.SetUrl($"https://items.test/{item.ItemKey}");
                http.AddHeader("x-item-key", item.ItemKey);
            }
        };
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(3), mapping: mapping);
        var templateUrl = ((HttpTask)harness.Template).Url;

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(3);

        foreach (var call in harness.Engine.Calls)
        {
            var prepared = call.PreparedTask.ShouldBeOfType<HttpTask>();
            prepared.Url.ShouldBe($"https://items.test/doc-{call.Order}");
            prepared.Headers.ShouldNotBeNull();
            prepared.Headers!.Value.GetProperty("x-item-key").GetString().ShouldBe($"doc-{call.Order}");
        }

        // Per-item URLs are all distinct — a shared instance would leave three identical ones.
        harness.Engine.Calls.Select(c => ((HttpTask)c.PreparedTask!).Url).Distinct().Count().ShouldBe(3);

        // And the template the executor cloned from is untouched, so the next batch (or the next
        // instance sharing the cached component) still starts from the authored configuration.
        ((HttpTask)harness.Template).Url.ShouldBe(templateUrl);
    }

    [Fact]
    public async Task OutputHandler_IsCalledExactlyOnce_AndItsDataReplacesTheDefaultPackaging()
    {
        var mapping = new StubFanOutMapping
        {
            OutputData = new Dictionary<string, object?>
            {
                ["report"] = "custom",
                ["processed"] = 4
            }
        };
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(4), mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(4);

        // ONE call for four items. The count is the invariant: the batch has a single write point,
        // so a per-item output handler would be four merges racing on the same instance data.
        mapping.OutputCalls.Count.ShouldBe(1);

        var output = harness.OutputAsJson((object?)response.Value!.Data);
        output.GetProperty("report").GetString().ShouldBe("custom");
        output.GetProperty("processed").GetInt32().ShouldBe(4);

        // The handler's data REPLACES the default shape rather than being merged alongside it —
        // an author who returns their own summary must not also get the raw result array.
        output.TryGetProperty(harness.ResultKey, out _).ShouldBeFalse();
        output.TryGetProperty($"{harness.ResultKey}Summary", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task MappingWithoutAnOutputHandler_GetsTheDefaultPackaging_ByteEquivalentToTheNoMappingCase()
    {
        // The defect this pins: a fan-out over an HTTP inner task REQUIRES an ItemInputHandler
        // (only a mapping can mutate the cloned task's URL/body), and supplying one used to opt the
        // author out of default output packaging — with OutputHandler abstract, they then had to
        // reimplement the documented default shape byte-for-byte in their .csx just to keep it.
        // Overriding input binding must cost nothing on the output side.
        var mapping = new InputOnlyFanOutMapping
        {
            BindItem = (task, _, item) => ((HttpTask)task).SetUrl($"https://items.test/{item.ItemKey}")
        };
        var mapped = new FanOutHarness(
            instanceData: FanOutHarness.Documents(4),
            mapping: mapping,
            joinPolicy: FanOutJoinPolicy.AllSettled);
        mapped.Engine.FailOrders.Add(2);

        var mappedResponse = await mapped.ExecuteAsync();

        // The same batch with NO mapping at all — the reference shape.
        var bare = new FanOutHarness(
            instanceData: FanOutHarness.Documents(4),
            joinPolicy: FanOutJoinPolicy.AllSettled);
        bare.Engine.FailOrders.Add(2);

        var bareResponse = await bare.ExecuteAsync();

        mapping.BoundItems.Count.ShouldBe(4);
        mapped.Engine.Calls.Count.ShouldBe(4);

        // The binding really happened — this is not the zero-script path in disguise.
        mapped.Engine.Calls.Select(c => ((HttpTask)c.PreparedTask!).Url).Distinct().Count().ShouldBe(4);

        // Byte-equivalent output, which is the actual guarantee: not "some default-looking shape",
        // but THE default shape, produced by the single packaging implementation both paths reach.
        // Only the wall-clock durations are normalised away.
        Normalize(mapped.OutputAsJson((object?)mappedResponse.Value!.Data), mapped.ResultKey)
            .ShouldBe(Normalize(bare.OutputAsJson((object?)bareResponse.Value!.Data), bare.ResultKey));

        // And it is the real packaging, not two empty objects compared to each other.
        var output = mapped.OutputAsJson((object?)mappedResponse.Value!.Data);
        output.GetProperty(mapped.ResultKey).GetArrayLength().ShouldBe(4);
        output.GetProperty($"{mapped.ResultKey}Summary").GetProperty("succeeded").GetInt32().ShouldBe(3);
        output.GetProperty($"{mapped.ResultKey}Summary").GetProperty("failed").GetInt32().ShouldBe(1);
    }

    /// <summary>
    /// Renders the default packaging as text with the per-item <c>durationMs</c> zeroed, so two runs
    /// of the same batch can be compared exactly rather than field by field.
    /// </summary>
    private static string Normalize(JsonElement output, string resultKey)
    {
        var node = JsonNode.Parse(output.GetRawText())!.AsObject();
        foreach (var item in node[resultKey]!.AsArray())
        {
            item!["durationMs"] = 0;
        }

        return node.ToJsonString();
    }

    [Fact]
    public async Task OutputHandler_ReceivesTheFullIndexOrderedResult_ForAMixedBatch()
    {
        // Two different failure shapes among five items, so the handler sees both a passed-through
        // inner error code and fan-out's own — the author branches on exactly this.
        var mapping = new StubFanOutMapping();
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(5),
            mapping: mapping,
            joinPolicy: FanOutJoinPolicy.AllSettled);
        harness.Engine.FailOrders.Add(1);
        harness.Engine.ThrowOrders.Add(3);

        await harness.ExecuteAsync();

        mapping.OutputCalls.Count.ShouldBe(1);
        var result = mapping.OutputCalls[0];

        result.Total.ShouldBe(5);
        result.Succeeded.ShouldBe(3);
        result.Failed.ShouldBe(2);
        result.TimedOut.ShouldBeFalse();

        // Index-ordered, regardless of the order the items actually settled in.
        result.Items.Count.ShouldBe(5);
        result.Items.Select(i => i.Index).ShouldBe([0, 1, 2, 3, 4]);
        result.Items.Select(i => i.ItemKey).ShouldBe(["doc-0", "doc-1", "doc-2", "doc-3", "doc-4"]);

        result.Items.Where(i => i.IsSuccess).Select(i => i.Index).ShouldBe([0, 2, 4]);
        result.Items.Where(i => i.IsSuccess).ShouldAllBe(i => (object?)i.Data != null);
        result.Items.Where(i => i.IsSuccess).ShouldAllBe(i => i.ErrorCode == null);

        // The inner task's own failure code passes through; a thrown item gets fan-out's.
        result.Items[1].IsSuccess.ShouldBeFalse();
        result.Items[1].ErrorCode.ShouldBe("Item:Failed");
        result.Items[3].IsSuccess.ShouldBeFalse();
        result.Items[3].ErrorCode.ShouldBe(FanOutErrorCodes.ItemFailed);
        result.Items[3].ErrorMessage.ShouldNotBeNull();
        result.Items[3].ErrorMessage!.ShouldContain("doc-3");
    }

    [Fact]
    public async Task OutputHandler_StillRunsExactlyOnce_WhenTheJoinFails_AndItsDataStillLands()
    {
        // A failed join must still land the mapping's output: the error boundary and the flow's
        // auto-transitions branch on it. Returning nothing here would leave them blind.
        var mapping = new StubFanOutMapping
        {
            OutputData = new Dictionary<string, object?> { ["report"] = "partial" }
        };
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(2),
            mapping: mapping,
            joinPolicy: FanOutJoinPolicy.All);
        harness.Engine.FailOrders.Add(0);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage.ShouldNotBeNullOrWhiteSpace();

        mapping.OutputCalls.Count.ShouldBe(1);
        harness.OutputAsJson((object?)response.Value!.Data)
            .GetProperty("report").GetString().ShouldBe("partial");
    }

    [Fact]
    public async Task ItemSelector_HandsItsOwnValuesToTheItemHandler_WithoutARoundTrip()
    {
        // Anonymous CLR objects are the natural .csx authoring shape for a selector, and Project
        // passes the values through by reference rather than normalising them via JSON. An author
        // who returns typed objects must get typed objects back in the handler.
        var alpha = new { id = "alpha", weight = 1 };
        var beta = new { id = "beta", weight = 2 };
        var gamma = new { id = "gamma", weight = 3 };

        var mapping = new StubFanOutMapping { Items = [alpha, beta, gamma] };
        var harness = new FanOutHarness(itemsPath: null, mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        harness.Engine.Calls.Count.ShouldBe(3);

        var ordered = mapping.Bindings.OrderBy(b => b.Item.Index).ToList();
        ordered.Select(b => b.Item.ItemKey).ShouldBe(["alpha", "beta", "gamma"]);

        // The very instances the selector produced, not JSON-normalised copies.
        ReferenceEquals((object?)ordered[0].Item.Value, alpha).ShouldBeTrue();
        ReferenceEquals((object?)ordered[1].Item.Value, beta).ShouldBeTrue();
        ReferenceEquals((object?)ordered[2].Item.Value, gamma).ShouldBeTrue();

        // Each item still gets its own journal identity off the fan-out task's key.
        harness.Engine.Calls.Select(c => c.JournalTaskKey)
            .ShouldBe(["fan-out-docs#0", "fan-out-docs#1", "fan-out-docs#2"], ignoreOrder: true);
    }

    [Fact]
    public async Task ItemSelector_ReturningAnEmptyCollection_IsAnEmptyBatch_NotAMissingItemSource()
    {
        // "No selector" (null) and "selector selected nothing" (empty) are different answers, and
        // only null is a configuration error. Collapsing them would fail a legitimate no-op batch.
        var mapping = new StubFanOutMapping { Items = [] };
        var harness = new FanOutHarness(itemsPath: null, mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeTrue();
        response.Value!.ErrorMessage.ShouldBeNull();
        harness.Engine.Calls.ShouldBeEmpty();
        mapping.BoundItems.ShouldBeEmpty();

        // The single write point still fires exactly once, with an empty batch to report.
        mapping.OutputCalls.Count.ShouldBe(1);
        mapping.OutputCalls[0].Total.ShouldBe(0);
        mapping.OutputCalls[0].Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AmbiguousItemSource_StopsBeforeTheItemHandlerAndTheOutputHandler()
    {
        // The ambiguity message and the "no engine calls" guard are pinned by
        // FanOutTaskExecutorTests; what matters here is that the MAPPING is not driven any further.
        // The selector has already run by then (documented trade-off), but nothing after it may.
        var mapping = new StubFanOutMapping { Items = [new { id = "from-selector" }] };
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(2), mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        mapping.SelectorContexts.Count.ShouldBe(1);
        mapping.BoundItems.ShouldBeEmpty();
        // No output handler on a configuration failure: there is no batch to report on, and
        // calling it would write a bogus zero-item result into instance data.
        mapping.OutputCalls.ShouldBeEmpty();
        ((object?)response.Value!.Data).ShouldBeNull();
    }

    [Fact]
    public async Task ItemInputHandlerThatThrows_FailsOnlyThatItem_AndNeverReachesTheEngine()
    {
        var mapping = new StubFanOutMapping
        {
            ItemInputHandlerThrows = item =>
                item.Index == 1 ? new InvalidOperationException("binding blew up") : null
        };
        var harness = new FanOutHarness(
            instanceData: FanOutHarness.Documents(3),
            mapping: mapping,
            joinPolicy: FanOutJoinPolicy.AllSettled);

        var response = await harness.ExecuteAsync();

        // One bad binding must not take the batch down: under allSettled the task still succeeds.
        response.Value!.IsSuccess.ShouldBeTrue();

        // The throw happens BEFORE the engine call, so the failed item never executed anything —
        // a binding that could not be built must not be sent to the inner task half-bound.
        harness.Engine.Calls.Select(c => c.Order).OrderBy(o => o).ShouldBe([0, 2]);

        mapping.OutputCalls.Count.ShouldBe(1);
        var result = mapping.OutputCalls[0];
        result.Total.ShouldBe(3);
        result.Succeeded.ShouldBe(2);
        result.Failed.ShouldBe(1);

        result.Items[1].IsSuccess.ShouldBeFalse();
        result.Items[1].ErrorCode.ShouldBe(FanOutErrorCodes.ItemFailed);
        result.Items[1].ErrorMessage!.ShouldContain("doc-1");
        result.Items[1].ErrorMessage!.ShouldContain("binding blew up");
        ((object?)result.Items[1].Data).ShouldBeNull();
    }

    [Fact]
    public async Task OutputHandlerThatThrows_FailsTheWholeTask_WithNoOutputToFallBackOn()
    {
        // The items all succeeded, but the batch's only write point did not produce anything.
        // Silently substituting the default packaging would hand the flow a shape its author never
        // wrote and its downstream mappings do not expect.
        var mapping = new StubFanOutMapping
        {
            OutputHandlerThrows = new InvalidOperationException("output blew up")
        };
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(2), mapping: mapping);

        var response = await harness.ExecuteAsync();

        harness.Engine.Calls.Count.ShouldBe(2);
        mapping.OutputCalls.Count.ShouldBe(1);

        response.IsSuccess.ShouldBeTrue();
        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("output handler", Case.Insensitive);
        response.Value!.ErrorMessage!.ShouldContain("output blew up");
        ((object?)response.Value!.Data).ShouldBeNull();
    }

    [Fact]
    public async Task ItemSelectorThatThrows_FailsTheTask_BeforeAnyItemRuns()
    {
        var mapping = new StubFanOutMapping
        {
            ItemSelectorThrows = new InvalidOperationException("selector blew up")
        };
        var harness = new FanOutHarness(itemsPath: null, mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("item selector", Case.Insensitive);
        response.Value!.ErrorMessage!.ShouldContain("selector blew up");

        // Nothing downstream of the item source may run once the source itself is broken.
        harness.Engine.Calls.ShouldBeEmpty();
        mapping.BoundItems.ShouldBeEmpty();
        mapping.OutputCalls.ShouldBeEmpty();
        ((object?)response.Value!.Data).ShouldBeNull();
    }

    [Fact]
    public async Task ItemSelectorThatThrows_FailsTheTask_EvenWhenItemsPathWouldHaveResolved()
    {
        // The selector runs unconditionally, before the itemsPath branch. A broken selector must
        // therefore fail the task rather than being quietly ignored because itemsPath could have
        // supplied the items anyway — silently falling back would hide the author's bug.
        var mapping = new StubFanOutMapping
        {
            ItemSelectorThrows = new InvalidOperationException("selector blew up")
        };
        var harness = new FanOutHarness(instanceData: FanOutHarness.Documents(3), mapping: mapping);

        var response = await harness.ExecuteAsync();

        response.Value!.IsSuccess.ShouldBeFalse();
        response.Value!.ErrorMessage!.ShouldContain("item selector", Case.Insensitive);
        harness.Engine.Calls.ShouldBeEmpty();
        mapping.OutputCalls.ShouldBeEmpty();
    }
}
