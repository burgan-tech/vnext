using System.Collections.Generic;
using System.Diagnostics;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the requested-vs-effective execution-mode trace tags (vnext#1003) written by
/// <see cref="InstanceCommandAppService.TagExecutionMode"/> — the observability deliverable that lets an
/// operator see when a flow/transition <c>executionType</c> definition overrode the caller's <c>sync</c>
/// query parameter. Tests the helper directly against a real recording <see cref="Activity"/>.
/// </summary>
public sealed class ExecutionModeTagTests
{
    private static Activity StartRecordingActivity(ActivitySource source, List<Activity> sink)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => sink.Add(a)
        };
        ActivitySource.AddActivityListener(listener);
        return source.StartActivity("test")!;
    }

    [Fact]
    public void TagExecutionMode_WhenOverridden_SetsRequestedEffectiveAndOverridden()
    {
        using var source = new ActivitySource("exec-tag-test-1");
        using var activity = StartRecordingActivity(source, new List<Activity>());

        // Caller asked ASYNC, transition definition forces SYNC → overridden.
        InstanceCommandAppService.TagExecutionMode(
            ExecMode.Async, ExecMode.Sync, ExecutionType.Sync, flowExecutionType: null);

        activity.GetTagItem("vnext.execution.requested").ShouldBe("ASYNC");
        activity.GetTagItem("vnext.execution.effective").ShouldBe("SYNC");
        activity.GetTagItem("vnext.execution.overridden").ShouldBe(true);
    }

    [Fact]
    public void TagExecutionMode_WhenNotOverridden_OmitsOverriddenTag()
    {
        using var source = new ActivitySource("exec-tag-test-2");
        using var activity = StartRecordingActivity(source, new List<Activity>());

        // No definition → not an override; requested == effective, overridden tag absent (not false).
        InstanceCommandAppService.TagExecutionMode(
            ExecMode.Async, ExecMode.Async, transitionExecutionType: null, flowExecutionType: null);

        activity.GetTagItem("vnext.execution.requested").ShouldBe("ASYNC");
        activity.GetTagItem("vnext.execution.effective").ShouldBe("ASYNC");
        activity.GetTagItem("vnext.execution.overridden").ShouldBeNull();
    }

    [Fact]
    public void TagExecutionMode_NoAmbientActivity_DoesNotThrow()
    {
        // With no listener/activity, Activity.Current is null — the helper must no-op silently.
        Activity.Current.ShouldBeNull();
        Should.NotThrow(() => InstanceCommandAppService.TagExecutionMode(
            ExecMode.Sync, ExecMode.Sync, null, null));
    }
}
