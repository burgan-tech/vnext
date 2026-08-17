using System;
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

    [Fact]
    public void ForSelfTarget_OnManual_ShouldExcludeOnlyTheStateLifecycleOrders()
    {
        var profile = PipelineExecutionProfile.ForSelfTarget(PipelineExecutionProfile.ForManual());

        var expected = new[]
        {
            LifecycleOrder.CancelScheduledJobs,
            LifecycleOrder.OnExit,
            LifecycleOrder.OnEntry,
            LifecycleOrder.Schedule,
        };
        profile.ExcludedStepOrders.OrderBy(x => x).ToArray().ShouldBe(expected.OrderBy(x => x).ToArray());
    }

    [Theory]
    [MemberData(nameof(AllBaseProfiles))]
    public void ForSelfTarget_ShouldPreserveBaseExclusionsAndFlags(PipelineExecutionProfile baseProfile)
    {
        var profile = PipelineExecutionProfile.ForSelfTarget(baseProfile);

        foreach (var order in baseProfile.ExcludedStepOrders)
            profile.ExcludedStepOrders.ShouldContain(order);

        profile.Name.ShouldBe($"{baseProfile.Name}+Self");
        profile.AllowAutoChain.ShouldBe(baseProfile.AllowAutoChain);
        profile.AllowSubFlow.ShouldBe(baseProfile.AllowSubFlow);
    }

    /// <summary>
    /// ChangeState is the only step that sets <c>context.Target</c>, which the auto step needs;
    /// OnExecute is the transition's own work. Excluding either would silently stop a $self
    /// transition from doing anything useful.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllBaseProfiles))]
    public void ForSelfTarget_ShouldNeverExcludeChangeStateOnExecuteOrAuto(PipelineExecutionProfile baseProfile)
    {
        var profile = PipelineExecutionProfile.ForSelfTarget(baseProfile);

        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.ChangeState);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.OnExecute);
        profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.CreateTransition);

        // The error-boundary profile excludes Auto by design; every other base must keep it.
        if (!baseProfile.ExcludedStepOrders.Contains(LifecycleOrder.Auto))
            profile.ExcludedStepOrders.ShouldNotContain(LifecycleOrder.Auto);
    }

    [Theory]
    [MemberData(nameof(AllBaseProfiles))]
    public void ForSelfTarget_ShouldReturnTheCachedVariant_ForKnownBaseProfiles(PipelineExecutionProfile baseProfile)
    {
        PipelineExecutionProfile.ForSelfTarget(baseProfile)
            .ShouldBeSameAs(PipelineExecutionProfile.ForSelfTarget(baseProfile));
    }

    [Fact]
    public void ForSelfTarget_WhenBaseProfileNull_ShouldThrowArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => PipelineExecutionProfile.ForSelfTarget(null!));
    }

    public static TheoryData<PipelineExecutionProfile> AllBaseProfiles() =>
    [
        PipelineExecutionProfile.ForManual(),
        PipelineExecutionProfile.ForAutoChain(),
        PipelineExecutionProfile.ForScheduled(),
        PipelineExecutionProfile.ForEvent(),
        PipelineExecutionProfile.ForErrorBoundary(),
    ];
}
