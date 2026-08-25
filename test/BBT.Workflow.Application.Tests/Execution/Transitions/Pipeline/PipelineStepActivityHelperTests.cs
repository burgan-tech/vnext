using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

// The detail level this class switches is process-global; see TracingDetailLevelCollection.

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Pins the always-on creation rule for pipeline-step spans: step spans are created in Business
/// mode too (Task 3). Names use the prefix-free "Step.{Name}" convention (trailing "Step" trimmed)
/// so Aether's BusinessSpanFilterProcessor — which only suppresses '['-prefixed DisplayNames —
/// never suppresses them, and their children keep a real exported parent.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class PipelineStepActivityHelperTests : IDisposable
{
    private readonly List<ActivityListener> _listeners = new();

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        Activity.Current = null;
    }

    private ActivityListener CreateListener(string sourceName, List<Activity> collected)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return listener;
    }

    [Fact]
    public void StartStepActivity_InBusinessMode_CreatesExportableSpan()
    {
        // Arrange: DetailLevel = Business (default), listener attached
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
        var step = new FakeStep(order: 50, name: "ChangeStateStep");

        // Act
        using (var activity = PipelineStepActivityHelper.StartStepActivity(step))
        {
            Assert.NotNull(activity);
            PipelineStepActivityHelper.SetStepOutcome(activity, StepOutcome.Continue());
        }

        // Assert
        var span = Assert.Single(collected);
        Assert.Equal("Step.ChangeState", span.DisplayName);            // Step suffix trimmed, no '[' prefix
        Assert.False(span.DisplayName.StartsWith("["));
        Assert.Equal(50, span.GetTagItem(TelemetryConstants.TagNames.StepOrder));
        Assert.Equal("continue", span.GetTagItem(TelemetryConstants.TagNames.StepOutcome));
        Assert.Equal(TelemetryConstants.SpanCategories.Business,
            span.GetTagItem(TelemetryConstants.TagNames.SpanCategory));
    }

    [Fact]
    public void SetStepOutcome_SkipTo_RecordsTargetOrder()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
        using (var activity = PipelineStepActivityHelper.StartStepActivity(new FakeStep(30, "RunOnExecuteTasksStep")))
        {
            PipelineStepActivityHelper.SetStepOutcome(activity, StepOutcome.SkipToFinalize());
        }

        Assert.Equal("skipTo:110", collected.Single().GetTagItem(TelemetryConstants.TagNames.StepOutcome));
    }

    [Fact]
    public void SetStepOutcome_Stop_RecordsStopTag()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
        using (var activity = PipelineStepActivityHelper.StartStepActivity(new FakeStep(5, "HandleCancelPreflightStep")))
        {
            PipelineStepActivityHelper.SetStepOutcome(activity, StepOutcome.Stop());
        }

        Assert.Equal("stop", collected.Single().GetTagItem(TelemetryConstants.TagNames.StepOutcome));
    }

    [Fact]
    public void SetStepError_RecordsErrorStatusAndMessage()
    {
        // Covers TransitionExecutor.ExecuteStepWithBoundaryAsync's two error paths (failed Result and
        // unhandled exception), both of which delegate to SetStepError with the failure message.
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);
        using (var activity = PipelineStepActivityHelper.StartStepActivity(new FakeStep(20, "CreateTransitionRecordStep")))
        {
            PipelineStepActivityHelper.SetStepError(activity, "boom");
        }

        var span = collected.Single();
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("boom", span.StatusDescription);
    }

    [Fact]
    public void SetStepError_NullActivity_IsNoOp()
    {
        Should.NotThrow(() => PipelineStepActivityHelper.SetStepError(null, "boom"));
    }

    private sealed class FakeStep : ITransitionStep
    {
        public FakeStep(int order, string name)
        {
            Order = order;
            Name = name;
        }

        public int Order { get; }

        public string Name { get; }

        public Task<Result<StepOutcome>> ExecuteAsync(
            TransitionExecutionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
    }
}
