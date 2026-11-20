using System;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The incident's stack trace is capped at the column size (vnext#1268): a script failure now stores
/// the exception's whole rendering, which can exceed it. The head — the exception type, its message and
/// the innermost frames — is what survives.
/// </summary>
public sealed class InstanceIncidentFactoryTests
{
    [Fact]
    public void Create_CapsTheStackTrace_AtTheColumnSize_KeepingItsHead()
    {
        var head = "System.InvalidOperationException: the input handler could not read the amount";
        var trace = head + new string('x', InstanceIncidentConstants.MaxStackTraceLength * 2);

        var incident = InstanceIncidentFactory.Create(
            "ready", "case-task-input", "eb-script-fail-task", "Script input handler failed", "Task:Script:x", "Task",
            stackTrace: trace);

        incident.StackTrace!.Length.ShouldBe(InstanceIncidentConstants.MaxStackTraceLength);
        incident.StackTrace.ShouldStartWith(head);
    }

    [Fact]
    public void Create_KeepsAStackTraceWithinTheCap_AsItIs()
    {
        const string trace = "System.ArgumentException: short\n   at Mapping.Handler(ScriptContext context) in :line 12";

        InstanceIncidentFactory.Create("ready", "t", null, "m", "c", "Pipeline", stackTrace: trace)
            .StackTrace.ShouldBe(trace);
    }
}
