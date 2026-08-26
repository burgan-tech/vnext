using System;
using System.Diagnostics;
using BBT.Aether.Telemetry;
using BBT.Workflow.Execution.Pipeline;
using Shouldly;
using Xunit;

// The detail level this class switches is process-global; see TracingDetailLevelCollection.

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline;

/// <summary>
/// Pins the state-lifecycle grouping spans (<c>OnExit.{state}</c> / <c>OnEntry.{state}</c>):
/// unlike the '['-prefixed step spans they are BUSINESS-level — created in Business mode too —
/// because they are what gives the state transition a shape in the default trace: without them
/// the OnExit/OnEntry task spans sit directly under <c>transition/{key}</c>, indistinguishable
/// from the transition's own OnExecute tasks.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class PipelineLifecycleActivityTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly AetherTracingDetailLevel _originalLevel = AetherTracingRuntime.DetailLevel;

    public PipelineLifecycleActivityTests()
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

    [Theory]
    [InlineData("OnExit", "pending-approval")]
    [InlineData("OnEntry", "approved")]
    public void ALifecycleSpan_IsCreatedInBusinessMode_UnderTheAmbientTransitionSpan(
        string operation, string stateKey)
    {
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);
        using var ambient = new Activity("transition/approve").Start();
        ambient.SetIdFormat(ActivityIdFormat.W3C);

        using var activity = PipelineStepActivityHelper.StartLifecycleActivity(operation, stateKey, 2);

        activity.ShouldNotBeNull();
        activity!.OperationName.ShouldBe($"{operation}.{stateKey}");
        activity.TraceId.ShouldBe(ambient.TraceId);
        activity.ParentSpanId.ShouldBe(ambient.SpanId);
        activity.GetTagItem("vnext.span.category").ShouldBe("business");
        activity.GetTagItem("vnext.state.key").ShouldBe(stateKey);
        activity.GetTagItem("vnext.task.count").ShouldBe(2);
    }

    [Fact]
    public void ALifecycleSpan_BecomesCurrent_SoTaskSpansNestUnderIt()
    {
        // The whole point of the group: task spans started inside the lifecycle phase must parent
        // to it, not to the transition span.
        AetherTracingRuntime.Configure(AetherTracingDetailLevel.Business);

        using var activity = PipelineStepActivityHelper.StartLifecycleActivity("OnEntry", "approved", 1);

        Activity.Current.ShouldBe(activity);
    }
}
