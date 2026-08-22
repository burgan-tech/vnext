using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting;

public class FanOutMappingContractTests
{
    /// <summary>
    /// The smallest mapping the contract admits. It implements ONLY <c>ItemInputHandler</c> — that
    /// this compiles at all is the point: the primary use case (a fan-out over an HTTP inner task)
    /// needs per-item binding and nothing else, and must not be forced to reimplement the runtime's
    /// default output packaging to get it.
    /// </summary>
    private sealed class MinimalMapping : IFanOutMapping
    {
        public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
            => Task.FromResult(new ScriptResponse());
    }

    [Fact]
    public async Task Default_ItemSelector_Should_Return_Null()
    {
        IFanOutMapping mapping = new MinimalMapping();
        var items = await mapping.ItemSelector(null!);
        items.ShouldBeNull();
    }

    [Fact]
    public async Task Default_OutputHandler_Should_Return_Null_Meaning_Use_Default_Packaging()
    {
        // Null is the "I did not override this" signal the executor keys the default packaging off,
        // mirroring ItemSelector's "use itemsPath". A default returning an empty ScriptResponse
        // instead would be indistinguishable from an author deliberately writing nothing.
        IFanOutMapping mapping = new MinimalMapping();
        var response = await mapping.OutputHandler(null!, new FanOutResult(0, 0, 0, false, []));
        response.ShouldBeNull();
    }

    [Fact]
    public void FanOutResult_Should_Carry_Counts_And_Items()
    {
        var items = new List<FanOutItemResult>
        {
            new(0, "a", true, null, null, null, TimeSpan.FromMilliseconds(10)),
            new(1, "b", false, null, "Task:500", "boom", TimeSpan.FromMilliseconds(20))
        };
        var result = new FanOutResult(2, 1, 1, false, items);
        result.Succeeded.ShouldBe(1);
        result.Items[1].ErrorCode.ShouldBe("Task:500");
    }
}
