using System.Diagnostics;
using BBT.Workflow.Logging;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Logging;

public sealed class ActivityExtensionsTests
{
    [Fact]
    public void Suppressed_pipeline_step_does_not_rename_parent_activity()
    {
        using var parent = new Activity("TransitionExecutor.ExecuteOneAsync").Start();

        parent.SetDisplayName("[5] CreateTransitionRecordStep");

        Assert.Equal("TransitionExecutor.ExecuteOneAsync", parent.DisplayName);
    }

    [Fact]
    public void Pipeline_step_activity_keeps_verbose_display_name()
    {
        using var step = new Activity("CreateTransitionRecordStep.ExecuteAsync").Start();

        step.SetDisplayName("[5] CreateTransitionRecordStep");

        Assert.Equal("[5] CreateTransitionRecordStep", step.DisplayName);
    }
}
