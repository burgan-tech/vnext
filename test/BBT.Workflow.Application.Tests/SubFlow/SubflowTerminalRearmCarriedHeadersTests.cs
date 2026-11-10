using System;
using System.Collections.Generic;
using BBT.Workflow.SubFlow;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

/// <summary>
/// A re-arm republishes "the same fact the original delivery carried"; the headers the settling
/// request carried must survive that rebuild, or a re-armed terminal event loses them.
/// </summary>
public sealed class SubflowTerminalRearmCarriedHeadersTests
{
    private static Dictionary<string, string?> Headers() => new() { ["x-a"] = "1", ["x-b"] = null };

    [Fact]
    public void Rearm_Rebuild_Preserves_CarriedHeaders_Completed()
    {
        var input = new FlowCompletedInput
        {
            InstanceId = Guid.NewGuid(), Domain = "d", Flow = "f", Version = "1.0.0",
            SubInstanceId = Guid.NewGuid(), CompletedState = "done", CompletedAt = DateTime.UtcNow,
            CarriedHeaders = Headers()
        };

        var evt = SubflowCompletionService.BuildTerminalEventFromInput(input, rearmAttempt: 1);

        evt.CarriedHeaders.ShouldBe(input.CarriedHeaders);
        evt.RearmAttempt.ShouldBe(1);
    }

    [Fact]
    public void Rearm_Rebuild_Preserves_CarriedHeaders_Faulted()
    {
        var input = new SubFlowFaultedInput
        {
            InstanceId = Guid.NewGuid(), Domain = "d", Flow = "f", Version = "1.0.0",
            SubInstanceId = Guid.NewGuid(), FaultedState = "error", FaultedAt = DateTime.UtcNow,
            CarriedHeaders = Headers()
        };

        var evt = SubflowFaultService.BuildTerminalEventFromInput(input, rearmAttempt: 1);

        evt.CarriedHeaders.ShouldBe(input.CarriedHeaders);
    }

    [Fact]
    public void Rearm_Rebuild_Preserves_CarriedHeaders_Canceled()
    {
        var input = new SubItemCanceledInput
        {
            InstanceId = Guid.NewGuid(), Domain = "d", Flow = "f", Version = "1.0.0",
            SubInstanceId = Guid.NewGuid(), CanceledState = "canceled", CanceledAt = DateTime.UtcNow,
            CarriedHeaders = Headers()
        };

        var evt = SubflowCancellationService.BuildTerminalEventFromInput(input, rearmAttempt: 1);

        evt.CarriedHeaders.ShouldBe(input.CarriedHeaders);
    }
}
