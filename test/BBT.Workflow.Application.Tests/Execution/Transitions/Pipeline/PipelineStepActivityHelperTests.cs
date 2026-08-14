using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Telemetry;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Pins the creation rule for pipeline-step spans: in Business mode NO step Activity may be
/// created (a created-but-export-filtered '[' span would orphan every child started inside the
/// step body); in Verbose mode the step span exists with the '[{Order}] {StepName}' convention.
/// </summary>
public sealed class PipelineStepActivityHelperTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly AetherTracingDetailLevel _originalLevel = AetherTracingRuntime.DetailLevel;

    public PipelineStepActivityHelperTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.Pipeline",
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

    [Fact]
    public void StartStepActivity_InBusinessMode_CreatesNoActivityAndKeepsAmbientCurrent()
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        using var ambient = new Activity("transition/start").Start();

        var activity = PipelineStepActivityHelper.StartStepActivity(new FakeStep());

        activity.ShouldBeNull();
        Activity.Current.ShouldBe(ambient);
    }

    [Fact]
    public void StartStepActivity_InVerboseMode_CreatesStepSpanUnderAmbient()
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Verbose);
        using var ambient = new Activity("transition/start").Start();
        ambient.SetIdFormat(ActivityIdFormat.W3C);

        using var activity = PipelineStepActivityHelper.StartStepActivity(new FakeStep());

        activity.ShouldNotBeNull();
        activity!.DisplayName.ShouldBe("[60] FakeStep");
        // Explicit parentContext parenting: identity is carried via ids, not the Parent reference.
        activity.TraceId.ShouldBe(ambient.TraceId);
        activity.ParentSpanId.ShouldBe(ambient.SpanId);
        activity.GetTagItem("vnext.span.category").ShouldBe("diagnostic");
    }

    private sealed class FakeStep : ITransitionStep
    {
        public int Order => 60;

        public string Name => "FakeStep";

        public Task<Result<StepOutcome>> ExecuteAsync(
            TransitionExecutionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result<StepOutcome>.Ok(StepOutcome.Continue()));
    }
}
