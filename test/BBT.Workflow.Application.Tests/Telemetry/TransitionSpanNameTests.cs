using BBT.Workflow.Telemetry;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the transition span naming. The name is an APM transaction name, so its shape is both a
/// readability decision and a cardinality one.
/// </summary>
public class TransitionSpanNameTests
{
    [Fact]
    public void Build_JoinsPrefixDomainFlowAndTransition()
    {
        TransitionSpanName.Build(TransitionSpanName.JobPrefix, "banking", "loan-application", "approve")
            .ShouldBe("TransitionJob.Execute/banking/loan-application/approve");
    }

    [Fact]
    public void Build_UsesTheSameShapeForBothPrefixes()
    {
        // Job and inline hops must be distinguishable by prefix and otherwise identical, so one
        // dashboard query shape covers both AutoTransitionMode settings.
        TransitionSpanName.Build(TransitionSpanName.HopPrefix, "banking", "loan-application", "approve")
            .ShouldBe("Transition.Hop/banking/loan-application/approve");
    }

    [Theory]
    // A gap truncates rather than emitting an empty segment: "prefix//flow/go" reads as a bug in the
    // tracing code and would split one transaction group in two.
    [InlineData(null, "flow", "go", "TransitionJob.Execute")]
    [InlineData("", "flow", "go", "TransitionJob.Execute")]
    [InlineData("   ", "flow", "go", "TransitionJob.Execute")]
    [InlineData("dom", null, "go", "TransitionJob.Execute/dom")]
    [InlineData("dom", "", "go", "TransitionJob.Execute/dom")]
    [InlineData("dom", "flow", null, "TransitionJob.Execute/dom/flow")]
    [InlineData("dom", "flow", "", "TransitionJob.Execute/dom/flow")]
    public void Build_SkipsMissingSegments(string? domain, string? flow, string? transitionKey, string expected)
    {
        TransitionSpanName.Build(TransitionSpanName.JobPrefix, domain, flow, transitionKey)
            .ShouldBe(expected);
    }

    [Fact]
    public void Build_CarriesNothingPerInstance()
    {
        // Guards the cardinality contract: every segment is a definition-level identifier. Appending
        // an instance id here would turn one APM transaction into one per instance.
        var name = TransitionSpanName.Build(
            TransitionSpanName.JobPrefix, "banking", "loan-application", "approve");

        name.Split('/').Length.ShouldBe(4);
    }
}
