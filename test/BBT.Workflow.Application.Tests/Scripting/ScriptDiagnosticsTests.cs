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
}
