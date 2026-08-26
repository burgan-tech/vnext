using System;
using System.Diagnostics;
using BBT.Aether.Telemetry;
using BBT.Workflow.Tasks.Coordinator;
using Shouldly;
using Xunit;

// The detail level this class switches is process-global; see TracingDetailLevelCollection.

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Pins the gating contract of the task-phase spans: <c>Task.PrepareInput</c> /
/// <c>Task.Invoke</c> / <c>Task.ProcessOutput</c> and <c>FanOut.Item</c> are BUSINESS-level —
/// created in Business mode too. They are the only spans carrying a binding's mapping cost, a
/// script task's compile+run time (its script runs inside Task.Invoke) and a fan-out item's
/// queue wait, so gating them on Verbose hid exactly what the default production configuration
/// needs. Noise control lives at the call site instead: TaskExecutorBase opens
/// PrepareInput/ProcessOutput only when the task has mapping code.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class TaskPhaseActivityTests : IDisposable
{
    /// <summary>
    /// Literal on purpose — reading the helper's static ActivitySource from inside the listener
    /// predicate would run its static initializer re-entrantly under the listener lock (see
    /// FanOutTraceCapture for the full story).
    /// </summary>
    private const string TaskSourceName = "BBT.Workflow.Tasks";

    private readonly ActivityListener _listener;
    private readonly AetherTracingDetailLevel _originalLevel = AetherTracingRuntime.DetailLevel;

    public TaskPhaseActivityTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        AetherTracingRuntime.Configure(_originalLevel);
        _listener.Dispose();
        Activity.Current = null;
    }

    [Theory]
    [InlineData(TaskExecutionActivityHelper.OperationPrepareInput)]
    [InlineData(TaskExecutionActivityHelper.OperationInvoke)]
    [InlineData(TaskExecutionActivityHelper.OperationProcessOutput)]
    public void APhaseSpan_IsCreatedInBusinessMode_AsABusinessSpanUnderTheAmbient(string operation)
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        using var ambient = new Activity("Task.Execute.send-mail").Start();
        ambient.SetIdFormat(ActivityIdFormat.W3C);

        using var activity = TaskExecutionActivityHelper.StartActivity(operation, "send-mail", "Http");

        activity.ShouldNotBeNull();
        activity!.OperationName.ShouldBe(operation);
        activity.TraceId.ShouldBe(ambient.TraceId);
        activity.ParentSpanId.ShouldBe(ambient.SpanId);
        activity.GetTagItem("vnext.span.category").ShouldBe("business");
        activity.GetTagItem("vnext.task.key").ShouldBe("send-mail");
    }

    [Fact]
    public void AFanOutItemSpan_IsCreatedInBusinessMode()
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);

        using var activity = TaskExecutionActivityHelper.StartFanOutItemActivity("fan-out-docs", "FanOut");

        activity.ShouldNotBeNull();
        activity!.OperationName.ShouldBe(TaskExecutionActivityHelper.OperationFanOutItem);
        activity.GetTagItem("vnext.span.category").ShouldBe("business");
    }

    [Fact]
    public void NoPhaseName_OptsIntoTheBusinessExportFilter()
    {
        // Aether's Business profile drops '['-prefixed spans at export. A phase span created in
        // Business mode with such a name would orphan its children — so no phase name may ever
        // start with '['. Pinned here because the names are consts a refactor could touch.
        foreach (var name in new[]
                 {
                     TaskExecutionActivityHelper.OperationPrepareInput,
                     TaskExecutionActivityHelper.OperationInvoke,
                     TaskExecutionActivityHelper.OperationProcessOutput,
                     TaskExecutionActivityHelper.OperationFanOutItem,
                     TaskExecutionActivityHelper.OperationTriggerLocal
                 })
        {
            name.ShouldNotStartWith("[");
        }
    }
}
