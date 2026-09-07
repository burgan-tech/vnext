using System;
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

    [Fact]
    public void SetError_WithException_SetsStatusAndErrorType()
    {
        using var source = new ActivitySource("Test.ActivityExtensions");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Test.ActivityExtensions",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("TestOp");
        var ex = new InvalidOperationException("Something went wrong");

        activity.SetError(ex);

        Assert.NotNull(activity);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Something went wrong", activity.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, activity.GetTagItem(TelemetryConstants.TagNames.ErrorType));
    }

    [Fact]
    public void SetError_WithMessageAndCode_SetsTagsAndStatus()
    {
        using var source = new ActivitySource("Test.ActivityExtensions.Message");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Test.ActivityExtensions.Message",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("TestOp2");

        activity.SetError("Failure occurred", errorType: "ValidationError", errorCode: "400");

        Assert.NotNull(activity);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Failure occurred", activity.StatusDescription);
        Assert.Equal("ValidationError", activity.GetTagItem(TelemetryConstants.TagNames.ErrorType));
        Assert.Equal("400", activity.GetTagItem(TelemetryConstants.TagNames.ErrorCode));
    }
}
