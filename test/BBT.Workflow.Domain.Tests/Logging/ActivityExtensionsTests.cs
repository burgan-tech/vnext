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

    // Activity.SetStatus REPLACES the previous status, so a handler's closing Ok used to erase an
    // error a nested call had recorded on the same span.
    [Fact]
    public void SetOkUnlessError_keeps_an_existing_error()
    {
        using var activity = new Activity("Job").Start();
        activity.SetStatus(ActivityStatusCode.Error, "pipeline faulted");

        activity.SetOkUnlessError();

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("pipeline faulted", activity.StatusDescription);
    }

    [Fact]
    public void SetOkUnlessError_sets_ok_when_nothing_failed()
    {
        using var activity = new Activity("Job").Start();

        activity.SetOkUnlessError();

        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    // A faulting pipeline still returns success (200 + Status=F); the fault has to reach the
    // enclosing spans, up to the local root and never past a remote/explicit parent.
    [Fact]
    public void MarkFaultedOnLocalChain_marks_every_in_process_ancestor_and_stops_at_the_local_root()
    {
        using var root = new Activity("TransitionJob.Execute/submit")
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom())
            .Start();
        using var strategy = new Activity("SyncTransitionStrategy.ExecuteAsync").Start();
        using var hop = new Activity("Transition.next").Start();

        hop.MarkFaultedOnLocalChain("Task:500", "task failed");

        foreach (var activity in new[] { hop, strategy, root })
        {
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Equal("Task:500", activity.GetTagItem(TelemetryConstants.TagNames.ErrorCode));
            Assert.Equal(true, activity.GetTagItem(TelemetryConstants.TagNames.InstanceFaulted));
        }

        Assert.Null(root.Parent);
    }

    [Fact]
    public void SetResultError_sets_status_code_and_type()
    {
        using var activity = new Activity("Op").Start();

        activity.SetResultError("Instance:100031", "busy", "InstanceBusy");

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("Instance:100031", activity.GetTagItem(TelemetryConstants.TagNames.ErrorCode));
        Assert.Equal("InstanceBusy", activity.GetTagItem(TelemetryConstants.TagNames.ErrorType));
    }
}
