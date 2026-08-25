using System;
using System.Diagnostics;
using System.Linq;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the compile-cost attribution contract: every script compilation accumulates
/// count / miss-count / total-ms tags onto the nearest task span (the span carrying
/// <c>vnext.task.key</c>), and only real compiles (cache misses) or failures emit a
/// <c>script.compile</c> event. This is what makes the compiler's share of
/// <c>Task.Execute.*</c> readable in APM without a dedicated compile span.
/// </summary>
public sealed class ScriptCompileTelemetryTests : IDisposable
{
    private const string SourceName = "BBT.Workflow.Tests.ScriptCompileTelemetry";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivityListener _listener;

    public ScriptCompileTelemetryTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
        Activity.Current = null;
    }

    private Activity StartTaskActivity()
    {
        var activity = _source.StartActivity("Task.Execute.test-task", ActivityKind.Internal);
        activity.ShouldNotBeNull();
        activity!.SetTag(TelemetryConstants.TagNames.TaskKey, "test-task");
        return activity;
    }

    [Fact]
    public void Record_Miss_TagsTaskSpan_AndEmitsCompileEvent()
    {
        using var task = StartTaskActivity();

        ScriptCompileTelemetry.Record(cacheMiss: true, durationMs: 1234.56, status: "success");

        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileCount).ShouldBe(1);
        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileMissCount).ShouldBe(1);
        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileTotalMs).ShouldBe(1234.6);

        var evt = task.Events.ShouldHaveSingleItem();
        evt.Name.ShouldBe(ScriptCompileTelemetry.CompileEventName);
        evt.Tags.Single(t => t.Key == "cache").Value.ShouldBe("miss");
        evt.Tags.Single(t => t.Key == "status").Value.ShouldBe("success");
    }

    [Fact]
    public void Record_Hit_AccumulatesTags_WithoutEvent()
    {
        using var task = StartTaskActivity();

        ScriptCompileTelemetry.Record(cacheMiss: true, durationMs: 1000, status: "success");
        ScriptCompileTelemetry.Record(cacheMiss: false, durationMs: 2, status: "success");
        ScriptCompileTelemetry.Record(cacheMiss: false, durationMs: 3, status: "success");

        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileCount).ShouldBe(3);
        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileMissCount).ShouldBe(1);
        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileTotalMs).ShouldBe(1005.0);

        // Only the miss produced an event — hits must not flood the span timeline.
        task.Events.Count().ShouldBe(1);
    }

    [Fact]
    public void Record_Failure_EmitsEvent_EvenOnHitPathStatus()
    {
        using var task = StartTaskActivity();

        ScriptCompileTelemetry.Record(cacheMiss: true, durationMs: 50, status: "compilation_error");

        var evt = task.Events.ShouldHaveSingleItem();
        evt.Tags.Single(t => t.Key == "status").Value.ShouldBe("compilation_error");
    }

    [Fact]
    public void Record_FromNestedChildSpan_WalksUpToTheTaskSpan()
    {
        using var task = StartTaskActivity();
        // Ambient-parented child (Parent chain intact) — e.g. an inner helper scope.
        using var child = _source.StartActivity("inner", ActivityKind.Internal);
        child.ShouldNotBeNull();

        ScriptCompileTelemetry.Record(cacheMiss: false, durationMs: 5, status: "success");

        // Attribution lands on the TASK span, not the innermost child.
        task.GetTagItem(TelemetryConstants.TagNames.ScriptCompileCount).ShouldBe(1);
        child!.GetTagItem(TelemetryConstants.TagNames.ScriptCompileCount).ShouldBeNull();
    }

    [Fact]
    public void Record_WithoutTaskAncestor_FallsBackToCurrentActivity()
    {
        using var plain = _source.StartActivity("state-function", ActivityKind.Internal);
        plain.ShouldNotBeNull();

        ScriptCompileTelemetry.Record(cacheMiss: false, durationMs: 7, status: "success");

        plain!.GetTagItem(TelemetryConstants.TagNames.ScriptCompileCount).ShouldBe(1);
    }

    [Fact]
    public void Record_WithNoActivity_DoesNotThrow()
    {
        Activity.Current = null;
        Should.NotThrow(() => ScriptCompileTelemetry.Record(cacheMiss: true, durationMs: 1, status: "success"));
    }
}
