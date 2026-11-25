using System;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

public sealed class ScriptDiagnosticsTests
{
    [Fact]
    public void Explain_OpenGenericAnonymousType_ReturnsActionableGuidance()
    {
        var ex = new ArgumentException(
            "Cannot create an instance of <>f__AnonymousType0`1[<creditBureau>j__TPar] " +
            "because Type.ContainsGenericParameters is true.");

        var message = ScriptDiagnostics.Explain(ex);

        message.ShouldContain("CreateObject()");
        message.ShouldContain("(object?)");
        message.ShouldContain("Original error:");
        message.ShouldContain(ex.Message);
    }

    [Fact]
    public void Explain_MarkerInInnerException_IsDetected()
    {
        var inner = new InvalidOperationException("... Type.ContainsGenericParameters is true.");
        var ex = new InvalidOperationException("Script output handler failed", inner);

        var message = ScriptDiagnostics.Explain(ex);

        message.ShouldContain("anonymous type");
    }

    [Fact]
    public void Explain_UnrelatedException_ReturnsOriginalMessageUnchanged()
    {
        var ex = new InvalidOperationException("Something else went wrong");

        var message = ScriptDiagnostics.Explain(ex);

        message.ShouldBe("Something else went wrong");
    }

    [Fact]
    public void TaskScriptError_KeepsTheCauseOnTheContext_AndBuildsTheExecutorsMessage()
    {
        var context = new ScriptContext.Builder(Microsoft.Extensions.Logging.Abstractions.NullLogger<ScriptContext>.Instance).Build();
        var thrown = new InvalidOperationException("could not read the amount", new FormatException("not a number"));

        var error = ScriptDiagnostics.TaskScriptError(context, thrown, "Http task input handler failed");

        error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        error.Message.ShouldBe("Http task input handler failed: could not read the amount");
        error.Detail.ShouldBeNull("the exception never travels in the Error: its detail reaches HTTP error bodies");
        context.FailureCause.ShouldBeSameAs(thrown);
        context.TakeFailureCause().ShouldBeSameAs(thrown);
        context.FailureCause.ShouldBeNull();
    }

    [Fact]
    public void FailureCause_IsNotCopiedIntoAParallelBranch()
    {
        var context = new ScriptContext.Builder(Microsoft.Extensions.Logging.Abstractions.NullLogger<ScriptContext>.Instance).Build();
        context.FailureCause = new InvalidOperationException("parent's own");

        var branch = context.CreateParallelBranch();

        branch.FailureCause.ShouldBeNull();
    }

    [Fact]
    public void TaskScriptError_ForACancellation_ReportsTheSameError_ButLeavesNoCause()
    {
        var context = new ScriptContext.Builder(Microsoft.Extensions.Logging.Abstractions.NullLogger<ScriptContext>.Instance).Build();

        var error = ScriptDiagnostics.TaskScriptError(context, new OperationCanceledException("request aborted"), "Http task input handler failed");

        error.Message.ShouldBe("Http task input handler failed: request aborted");
        context.FailureCause.ShouldBeNull("a cancellation is not the script's failure");
    }
}
