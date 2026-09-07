using System;
using System.Diagnostics;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs.Handlers;

/// <summary>
/// Pins the two trace-restoration policies for background jobs:
/// <see cref="BackgroundJobActivityHelper.StartActivityContinuingTrace"/> (immediate jobs — the
/// payload's TraceParent becomes the REAL parent so the job stays inside the originating trace)
/// and <see cref="BackgroundJobActivityHelper.StartDeferredActivity"/> (deferred jobs —
/// ambient parent, original context retained as searchable tags).
/// </summary>
public sealed class BackgroundJobActivityHelperTests : IDisposable
{
    private readonly ActivityListener _listener;

    public BackgroundJobActivityHelperTests()
    {
        // Literal source name: referencing BackgroundJobActivityHelper.ActivitySource here would
        // re-enter the helper's type initializer while it registers with this very listener.
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.BackgroundJobs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    private static TransitionJobPayload CreatePayload(string? traceParent, string? traceState = null) => new()
    {
        JobName = "trans-x-go",
        InstanceId = Guid.NewGuid(),
        TransitionKey = "go",
        Domain = "core",
        Workflow = "flow-x",
        Version = "1.0.0",
        TraceParent = traceParent,
        TraceState = traceState
    };

    private static Activity StartAmbientActivity()
    {
        // A plain root Activity (no source needed) to act as the Dapr scheduler-callback span.
        var ambient = new Activity("dapr-callback");
        ambient.SetIdFormat(ActivityIdFormat.W3C);
        ambient.Start();
        return ambient;
    }

    [Fact]
    public void ContinuingTrace_WithTraceParentAndAmbient_ParentsOnPayloadAndTagsAmbient()
    {
        using var original = StartAmbientActivity();
        var payload = CreatePayload(original.Id, "vendor=state");
        var originalTraceId = original.Context.TraceId;
        original.Stop();

        using var ambient = StartAmbientActivity();

        using var activity = BackgroundJobActivityHelper.StartActivityContinuingTrace("TransitionJob.Execute", payload);

        activity.ShouldNotBeNull();
        activity.TraceId.ShouldBe(originalTraceId);
        activity.Links.ShouldBeEmpty();
        activity.GetTagItem(TelemetryConstants.TagNames.DaprCallback).ShouldBe(true);
        activity.GetTagItem(TelemetryConstants.TagNames.DaprCallbackTraceId)
            .ShouldBe(ambient.TraceId.ToString());
        activity.GetTagItem(TelemetryConstants.TagNames.DaprCallbackSpanId)
            .ShouldBe(ambient.SpanId.ToString());
    }

    [Fact]
    public void ContinuingTrace_WithTraceParentNoAmbient_ParentsOnPayloadWithoutLink()
    {
        using var original = StartAmbientActivity();
        var payload = CreatePayload(original.Id);
        var originalTraceId = original.Context.TraceId;
        original.Stop();
        Activity.Current = null;

        using var activity = BackgroundJobActivityHelper.StartActivityContinuingTrace("TransitionJob.Execute", payload);

        activity.ShouldNotBeNull();
        activity.TraceId.ShouldBe(originalTraceId);
        activity.Links.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-w3c-traceparent")]
    public void ContinuingTrace_WithMissingOrInvalidTraceParent_FallsBackToAmbientParent(string? traceParent)
    {
        using var ambient = StartAmbientActivity();
        var payload = CreatePayload(traceParent);

        using var activity = BackgroundJobActivityHelper.StartActivityContinuingTrace("TransitionJob.Execute", payload);

        activity.ShouldNotBeNull();
        activity.TraceId.ShouldBe(ambient.Context.TraceId);
        activity.Links.ShouldBeEmpty();
    }

    [Fact]
    public void DeferredActivity_UsesAmbientParentAndTagsPayloadWithoutLink()
    {
        using var original = StartAmbientActivity();
        var payload = CreatePayload(original.Id);
        var originalTraceId = original.Context.TraceId;
        original.Stop();

        using var ambient = StartAmbientActivity();

        using var activity = BackgroundJobActivityHelper.StartDeferredActivity("TransitionTimerJob.Execute", payload);

        activity.ShouldNotBeNull();
        activity.TraceId.ShouldBe(ambient.Context.TraceId);
        activity.Links.ShouldBeEmpty();
        activity.GetTagItem(TelemetryConstants.TagNames.OriginTraceId)
            .ShouldBe(originalTraceId.ToString());
        activity.GetTagItem(TelemetryConstants.TagNames.OriginSpanId)
            .ShouldBe(original.SpanId.ToString());
    }
}
