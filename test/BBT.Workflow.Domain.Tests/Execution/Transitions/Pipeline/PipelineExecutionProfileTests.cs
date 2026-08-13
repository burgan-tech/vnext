using System.Linq;
using BBT.Workflow;
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Pipeline;

/// <summary>
/// Unit tests for <see cref="PipelineExecutionProfile"/> factory methods and profile invariants.
/// </summary>
public class PipelineExecutionProfileTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void ForManual_ShouldHaveEmptyExcludedStepOrders()
    {
        var profile = PipelineExecutionProfile.ForManual();
        profile.ExcludedStepOrders.ShouldBeEmpty();
    }

    [Fact]
    public void ForManual_ShouldHaveExpectedNameAndFlags()
    {
        var profile = PipelineExecutionProfile.ForManual();
        profile.Name.ShouldBe("Manual");
        profile.AllowAutoChain.ShouldBeTrue();
        profile.AllowSubFlow.ShouldBeTrue();
    }

    [Fact]
    public void ForAutoChain_ShouldExcludeExpectedOrders()
    {
        var profile = PipelineExecutionProfile.ForAutoChain();
        var expected = new[]
        {
            LifecycleOrder.Preflight,
            LifecycleOrder.ForwardToActiveSubflow,
            LifecycleOrder.SetBusy,
            LifecycleOrder.ApplyTimeoutState,
        };
        profile.ExcludedStepOrders.OrderBy(x => x).ToArray().ShouldBe(expected);
        profile.ExcludedStepOrders.Count.ShouldBe(4);
        // ResourceLock must NOT be excluded: a `resourceLock` on an auto-chained transition must run.
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.ResourceLock);
    }

    [Fact]
    public void ForAutoChain_ShouldHaveExpectedNameAndFlags()
    {
        var profile = PipelineExecutionProfile.ForAutoChain();
        profile.Name.ShouldBe("AutoChain");
        profile.AllowAutoChain.ShouldBeTrue();
        profile.AllowSubFlow.ShouldBeFalse();
    }

    [Fact]
    public void ForScheduled_ShouldExcludeExpectedOrders()
    {
        var profile = PipelineExecutionProfile.ForScheduled();
        var expected = new[]
        {
            LifecycleOrder.Preflight,
            LifecycleOrder.ForwardToActiveSubflow,
        };
        profile.ExcludedStepOrders.OrderBy(x => x).ToArray().ShouldBe(expected);
        profile.ExcludedStepOrders.Count.ShouldBe(2);
    }

    [Fact]
    public void ForScheduled_ShouldHaveExpectedNameAndFlags()
    {
        var profile = PipelineExecutionProfile.ForScheduled();
        profile.Name.ShouldBe("Scheduled");
        profile.AllowAutoChain.ShouldBeTrue();
        profile.AllowSubFlow.ShouldBeFalse();
    }

    [Fact]
    public void ForEvent_ShouldExcludeExpectedOrders()
    {
        var profile = PipelineExecutionProfile.ForEvent();
        var expected = new[]
        {
            LifecycleOrder.Preflight,
            LifecycleOrder.ForwardToActiveSubflow,
        };
        profile.ExcludedStepOrders.OrderBy(x => x).ToArray().ShouldBe(expected);
        profile.ExcludedStepOrders.Count.ShouldBe(2);
    }

    [Fact]
    public void ForEvent_ShouldHaveExpectedNameAndFlags()
    {
        var profile = PipelineExecutionProfile.ForEvent();
        profile.Name.ShouldBe("Event");
        profile.AllowAutoChain.ShouldBeTrue();
        profile.AllowSubFlow.ShouldBeTrue();
    }

    [Fact]
    public void ForErrorBoundary_ShouldExcludeExpectedOrders()
    {
        var profile = PipelineExecutionProfile.ForErrorBoundary();
        var expected = new[]
        {
            LifecycleOrder.Preflight,
            LifecycleOrder.ForwardToActiveSubflow,
            LifecycleOrder.ResourceLock,
            LifecycleOrder.Schedule,
        };
        profile.ExcludedStepOrders.OrderBy(x => x).ToArray().ShouldBe(expected);
        profile.ExcludedStepOrders.Count.ShouldBe(4);
    }

    [Fact]
    public void ForErrorBoundary_ShouldHaveExpectedNameAndFlags()
    {
        var profile = PipelineExecutionProfile.ForErrorBoundary();
        profile.Name.ShouldBe("ErrorBoundary");
        profile.AllowAutoChain.ShouldBeTrue();
        profile.AllowSubFlow.ShouldBeFalse();
    }
}
