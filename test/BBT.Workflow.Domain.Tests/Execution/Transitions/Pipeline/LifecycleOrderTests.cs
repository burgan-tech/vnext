using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Pins the epilogue ordering contract: automatic transitions are evaluated BEFORE
/// scheduled transitions are armed, so a satisfied auto winner can suppress pointless
/// timer arming (which the next hop's CancelScheduledJobs would immediately tear down).
/// </summary>
public class LifecycleOrderTests
{
    [Fact]
    public void Auto_ShouldRunBeforeSchedule()
    {
        LifecycleOrder.Auto.ShouldBeLessThan(LifecycleOrder.Schedule);
    }

    [Fact]
    public void ClearBusyOnResume_ShouldRunBeforeEntireEpilogue()
    {
        // Subflow/long-poll resumes start from this step and must still walk BOTH
        // epilogue steps (Auto first, then Schedule).
        LifecycleOrder.ClearBusyOnResumeStep.ShouldBeLessThan(LifecycleOrder.Auto);
        LifecycleOrder.ClearBusyOnResumeStep.ShouldBeLessThan(LifecycleOrder.Schedule);
    }

    [Fact]
    public void LongPollTermination_ShouldRunBeforeEpilogue()
    {
        LifecycleOrder.LongPollTermination.ShouldBeLessThan(LifecycleOrder.ClearBusyOnResumeStep);
    }

    [Fact]
    public void Epilogue_ShouldRunBeforeFinishAndFinalize()
    {
        LifecycleOrder.Schedule.ShouldBeLessThan(LifecycleOrder.Finish);
        LifecycleOrder.Finish.ShouldBeLessThan(LifecycleOrder.Finalize);
    }
}
