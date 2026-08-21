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
    private sealed class MinimalMapping : IFanOutMapping
    {
        public Task<ScriptResponse> ItemInputHandler(WorkflowTask task, ScriptContext context, FanOutItem item)
            => Task.FromResult(new ScriptResponse());

        public Task<ScriptResponse> OutputHandler(ScriptContext context, FanOutResult result)
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
