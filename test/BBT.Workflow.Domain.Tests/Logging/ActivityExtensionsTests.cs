using System.Diagnostics;
using BBT.Workflow.Logging;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Logging;

public sealed class ActivityExtensionsTests
{
    // SetDisplayName is a plain rename. The old "suppressed step must not rename its parent"
    // guard is gone by design: pipeline steps no longer rename Activity.Current at all — their
    // spans are named at CREATION by PipelineStepActivityHelper and only exist in Verbose mode,
    // so the mis-rename scenario the guard defended against can no longer occur.
    [Fact]
    public void SetDisplayName_renames_the_activity()
    {
        using var activity = new Activity("TransitionExecutor.ExecuteOneAsync").Start();

        activity.SetDisplayName("transition/start");

        Assert.Equal("transition/start", activity.DisplayName);
    }

    [Fact]
    public void SetDisplayName_on_null_activity_is_a_no_op()
    {
        Activity? activity = null;

        Assert.Null(activity.SetDisplayName("anything"));
    }
}
